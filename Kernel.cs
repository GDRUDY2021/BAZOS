using BAZOS.Api;
using Cosmos.Kernel.Core.IO;
using System;
using System.IO;
using Sys = Cosmos.Kernel.System;

namespace BAZOS;

public class Kernel : Sys.Kernel
{

    protected override void BeforeRun()
    {
        Console.WriteLine("BAZOS booted successfully!");
        Shell.Init();
        Shell.RunCommand("mount");
    }

    protected override void Run()
    {
        Console.Write($"> ");
        var input = Console.ReadLine() ?? string.Empty;

        Shell.RunCommand(input);
    }
}