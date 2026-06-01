using System;
using System.IO;
using System.Text;
using BAZOS.Api;
using BAZOS.Drivers;

namespace BAZPacker
{
    class Program
    {
        static string _currentCompileDir = "";

        static void Main(string[] args)
        {
            Compiler.FileReader = (string path, out string content) =>
            {
                content = string.Empty;

                if (File.Exists(path)) { content = File.ReadAllText(path); return true; }

                string noSlash = path.TrimStart('/', '\\');
                if (File.Exists(noSlash)) { content = File.ReadAllText(noSlash); return true; }

                // 3. Пробуем найти относительно папки компилируемого скрипта
                if (!string.IsNullOrEmpty(_currentCompileDir))
                {
                    string relative = Path.Combine(_currentCompileDir, noSlash);
                    if (File.Exists(relative)) { content = File.ReadAllText(relative); return true; }
                }

                return false;
            };

            while (true)
            {
                Console.Clear();
                Console.WriteLine("=== BAZOS Toolchain (NativeAOT Ready) ===");
                Console.WriteLine("1. Pack folder to initrd.pack (OS Installer)");
                Console.WriteLine("2. Compile .bs to .bvx (User App)");
                Console.WriteLine("3. Compile .bs to .drv (OS Driver)");
                Console.WriteLine("0. Exit");
                Console.Write("\nSelect option: ");

                var k = Console.ReadKey(true);
                if (k.KeyChar == '1') PackInitrd();
                else if (k.KeyChar == '2') CompileBvx();
                else if (k.KeyChar == '3') CompileDrv();
                else if (k.KeyChar == '0') break;
            }
        }

        /*static void PackInitrd()
        {
            Console.Clear();
            Console.Write("Enter path to the Root folder to pack (e.g. C:\\BAZOS\\RootFS): ");
            string rootDir = Console.ReadLine()?.Trim('"');

            if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir))
            {
                Console.WriteLine($"\n[ERROR] Directory '{rootDir}' not found.");
                Console.ReadLine(); return;
            }

            string outFile = Path.Combine(rootDir, "initrd.pack");
            string[] files = Directory.GetFiles(rootDir, "*.*", SearchOption.AllDirectories);

            using FileStream fs = new FileStream(outFile, FileMode.Create);
            using BinaryWriter bw = new BinaryWriter(fs);

            bw.Write(Encoding.ASCII.GetBytes("PACK"));
            bw.Write(files.Length - 1);

            Console.WriteLine($"\nPacking files into {outFile}...\n");

            foreach (string file in files)
            {
                if (file.EndsWith("initrd.pack")) continue;

                string relativePath = file.Substring(rootDir.Length).Replace('\\', '/');
                if (!relativePath.StartsWith("/")) relativePath = "/" + relativePath;

                byte[] pathBytes = Encoding.UTF8.GetBytes(relativePath);
                byte[] fileData = File.ReadAllBytes(file);

                bw.Write(pathBytes.Length);
                bw.Write(pathBytes);
                bw.Write(fileData.Length);
                bw.Write(fileData);

                Console.WriteLine($" Packed: {relativePath} ({fileData.Length} bytes)");
            }

            Console.WriteLine("\n[OK] Packing complete! Press Enter to return.");
            Console.ReadLine();
        }*/

        static void PackInitrd()
        {
            Console.Clear();
            Console.Write("Enter path to the Root folder to pack (e.g. C:\\BAZOS\\RootFS): ");
            string rootDir = Console.ReadLine()?.Trim('"');

            if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir))
            {
                Console.WriteLine($"\n[ERROR] Directory '{rootDir}' not found.");
                Console.ReadLine(); return;
            }

            // Сначала собираем бинарные данные в память
            string[] files = Directory.GetFiles(rootDir, "*.*", SearchOption.AllDirectories);
            using MemoryStream ms = new MemoryStream();
            using BinaryWriter bw = new BinaryWriter(ms);

            bw.Write(Encoding.ASCII.GetBytes("PACK"));
            bw.Write(files.Length); // Считаем все файлы

            Console.WriteLine($"\nPacking {files.Length} files...\n");

            foreach (string file in files)
            {
                string relativePath = file.Substring(rootDir.Length).Replace('\\', '/');
                if (!relativePath.StartsWith("/")) relativePath = "/" + relativePath;

                byte[] pathBytes = Encoding.UTF8.GetBytes(relativePath);
                byte[] fileData = File.ReadAllBytes(file);

                bw.Write(pathBytes.Length);
                bw.Write(pathBytes);
                bw.Write(fileData.Length);
                bw.Write(fileData);

                Console.WriteLine($" Packed: {relativePath} ({fileData.Length} bytes)");
            }

            // Теперь генерируем C# файл для ядра BAZOS
            byte[] finalArchive = ms.ToArray();
            string outCsFile = Path.Combine(Directory.GetParent(rootDir).FullName, "InitrdPayload.cs");

            Console.WriteLine($"\nGenerating C# Payload: {outCsFile}...");

            using StreamWriter sw = new StreamWriter(outCsFile);
            sw.WriteLine("// AUTO-GENERATED BY BAZPacker");
            sw.WriteLine("namespace BAZOS.Core");
            sw.WriteLine("{");
            sw.WriteLine("    public static class InitrdPayload");
            sw.WriteLine("    {");
            sw.WriteLine($"        // Archive size: {finalArchive.Length} bytes");
            sw.WriteLine("        public static readonly byte[] Data = new byte[]");
            sw.WriteLine("        {");

            // Пишем байты красиво (по 16 штук в строке)
            for (int i = 0; i < finalArchive.Length; i++)
            {
                if (i % 16 == 0) sw.Write("            ");
                sw.Write($"0x{finalArchive[i]:X2}, ");
                if (i % 16 == 15) sw.WriteLine();
            }

            if (finalArchive.Length % 16 != 0) sw.WriteLine();
            sw.WriteLine("        };");
            sw.WriteLine("    }");
            sw.WriteLine("}");

            Console.WriteLine("\n[OK] C# Payload generated! Add 'InitrdPayload.cs' to your BAZOS project.");
            Console.ReadLine();
        }

        static void CompileBvx()
        {
            Console.Clear();
            Console.Write("Enter absolute path to .bs file: ");
            string inputPath = Console.ReadLine()?.Trim('"');

            if (!File.Exists(inputPath))
            {
                Console.WriteLine("\n[ERROR] File not found.");
                Console.ReadLine(); return;
            }

            _currentCompileDir = Path.GetDirectoryName(inputPath);
            string source = File.ReadAllText(inputPath);
            Console.WriteLine("\nCompiling...");

            if (!Compiler.TryCompile(source, out byte[] bytecode, out string error))
            {
                Console.WriteLine($"\n[ERROR] Compile failed:\n{error}");
                Console.ReadLine(); return;
            }

            string outPath = Path.ChangeExtension(inputPath, ".bvx");
            File.WriteAllBytes(outPath, bytecode);

            Console.WriteLine($"\n[OK] Compiled successfully to: {outPath}");
            Console.ReadLine();
        }

        static void CompileDrv()
        {
            Console.Clear();
            Console.Write("Enter absolute path to the driver script (main.bs): ");
            string inputPath = Console.ReadLine()?.Trim('"');

            if (!File.Exists(inputPath))
            {
                Console.WriteLine("\n[ERROR] File not found.");
                Console.ReadLine(); return;
            }

            _currentCompileDir = Path.GetDirectoryName(inputPath);
            string manifestPath = Path.Combine(_currentCompileDir, "manifest.txt");

            if (!File.Exists(manifestPath))
            {
                Console.WriteLine($"\n[ERROR] Missing 'manifest.txt' in folder: {_currentCompileDir}");
                Console.ReadLine(); return;
            }

            string source = File.ReadAllText(inputPath);
            Console.WriteLine("\nCompiling script...");

            if (!Compiler.TryCompile(source, out byte[] payload, out string error))
            {
                Console.WriteLine($"\n[ERROR] Compile failed:\n{error}");
                Console.ReadLine(); return;
            }

            Console.WriteLine("Packing driver...");
            byte[] manifest = File.ReadAllBytes(manifestPath);

            byte[] drvData = DriverPackageFormat.Pack(manifest, payload);

            string folderName = new DirectoryInfo(_currentCompileDir).Name;
            string outPath = Path.Combine(_currentCompileDir, folderName + ".drv");

            File.WriteAllBytes(outPath, drvData);

            Console.WriteLine($"\n[OK] Driver packed successfully to: {outPath}");
            Console.ReadLine();
        }
    }
}