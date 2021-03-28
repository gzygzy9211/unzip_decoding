using System;
using System.IO.Compression;
using System.IO;
using CommandLine;
using System.Text;
using System.Linq;
using Crypto = System.Security.Cryptography;

namespace unzip_decoding
{
    enum EncodingName
    {
        utf8 = 65001,
        cp936 = 936,   // GB2312
        gb3212 = 936,
        gbk = 936,
        ascii = 20127,
        mac = 10008, // mac chs
    }

    enum RenameType
    {
       none = 0,
       md5 = 1,
       sha1 = 2,
       sha256 = 3,
    }


    class Args
    {
        [Value(0, Required = true, HelpText = "Path to the Zip Archive")]
        public string FileName { get; set; }

        [Option('d', Required = false, HelpText = "Destination Directory", Default = ".")]
        public string Destination { get; set; }

        [Option('c', Required = false, HelpText = "Encoding of Entry Name in Zip\n", Default = EncodingName.utf8)]
        public EncodingName Coding { get; set; }

        [Option('t', Required = false, HelpText = "Number of Threads to Extract Files", Default = 1)]
        public int Threads { get; set; }

        [Option('q', Required = false, HelpText = "Do not Print File Name That Are Being Extracted", Default = false)]
        public bool Quiet { get; set; }

        [Option("rename", Required = false, HelpText = "Ignore Directory Structure and Rename Files into Its MD5 + Suffix\n",
                Default = RenameType.none)]
        public RenameType Rename { get; set; }
    }

    class Program
    {
        static Encoding get_encoding(EncodingName name)
        {
            var ret = CodePagesEncodingProvider.Instance.GetEncoding((int)name);
            if (ret == null)
            {
                ret = Encoding.GetEncoding((int)name);
            }
            return ret;
        }

        static string get_hash_string(byte[] buffer, RenameType type)
        {
            byte[] hash;
            switch (type)
            {
                case RenameType.md5: hash = Crypto.MD5.Create().ComputeHash(buffer); break;
                case RenameType.sha1: hash = Crypto.SHA1.Create().ComputeHash(buffer); break;
                case RenameType.sha256: hash = Crypto.SHA256.Create().ComputeHash(buffer); break;
                default: throw new NotSupportedException(type.ToString());
            }
            return new StringBuilder()
                .AppendJoin("", hash.Select(b => b.ToString("x").PadLeft(2, '0')))
                .ToString();
        }
        static void Main(string[] args)
        {
            ParserResult<Args> result = new Parser(config => config.AutoHelp = false).ParseArguments<Args>(args);
            result.WithNotParsed(errs => 
            {
                var help = CommandLine.Text.HelpText.AutoBuild(result, h => 
                { 
                    h.AddEnumValuesToHelpText = true;
                    return h;
                });
                Console.Error.WriteLine(help);
            }).WithParsed(opt =>
            {
                if (opt.Threads <= 0 || opt.Threads > 8)
                    throw new ArgumentException(nameof(opt.Threads));

                Console.Error.WriteLine("Processing " + opt.FileName);
                var dest_root = new DirectoryInfo(opt.Destination);
                if (!dest_root.Exists) Directory.CreateDirectory(dest_root.FullName);

                System.Threading.Tasks.Parallel.For(0, opt.Threads, 
                    new System.Threading.Tasks.ParallelOptions() { MaxDegreeOfParallelism = opt.Threads, },
                th => 
                {
                    using (var stream = new FileStream(opt.FileName, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, true, get_encoding(opt.Coding)))
                    {

                        foreach ((var idx, var ent) in zip.Entries.Select((ent, idx) => (idx, ent)))
                        {
                            if (idx % opt.Threads != th) continue;
                            
                            var dest_file = new FileInfo(opt.Destination + "/" + ent.FullName);
                            if (ent.FullName.EndsWith('/') || ent.FullName.EndsWith('\\') || ent.Name == "")
                            {  // is directory
                                Console.Error.WriteLine("Creating " + ent.FullName);
                                if (!dest_file.Exists && opt.Rename == RenameType.none)
                                    lock (dest_root)
                                    {
                                        Directory.CreateDirectory(dest_file.FullName);
                                    }
                                continue;
                            }
                            else 
                            {  // is file
                                var buffer = new byte[ent.Length];
                                using (var es = ent.Open())
                                {
                                    int act_len = es.Read(buffer, 0, buffer.Length);
                                    if (act_len != buffer.Length)
                                    {
                                        Console.Error.WriteLine("Cannot Read " + ent.FullName);
                                        continue;
                                    }
                                }
                                if (ent.Crc32 != Force.Crc32.Crc32Algorithm.Compute(buffer))
                                {
                                    Console.Error.WriteLine("Crc32 Check Fail " + ent.FullName);
                                    continue;
                                }

                                string dest_path;
                                if (opt.Rename == RenameType.none)
                                {
                                    Console.Error.WriteLine("Extracting " + ent.FullName);
                                    var dest_dir = dest_file.Directory;
                                    if (!dest_dir.Exists)
                                        lock (dest_root)
                                        {
                                            Directory.CreateDirectory(dest_dir.FullName);
                                        }
                                    dest_path = dest_file.FullName;
                                }
                                else
                                {
                                    string filename = get_hash_string(buffer, opt.Rename) + dest_file.Extension;
                                    dest_path = new FileInfo(opt.Destination + "/" + filename).FullName;
                                    Console.Error.WriteLine("Extracting " + filename + " from " + ent.FullName);
                                }
                                using (var ws = new FileStream(dest_path, FileMode.Create, FileAccess.Write, FileShare.Write))
                                    ws.Write(buffer, 0, buffer.Length);
                            }
                            
                        }
                    }
                });
                
            });
        }
    }
}
