using BAZOS.Api;
using BAZOS.Drivers;
using Cosmos.Kernel.Core.IO;
using System;
using System.IO;
using Sys = Cosmos.Kernel.System;

namespace BAZOS;

public class Kernel : Sys.Kernel
{
    protected override void BeforeRun()
    {
        Shell.Init();
        Shell.RunCommand("device /disk=current /m");
    }

    private int _tick = 0;

    protected override void Run()
    {
        _tick++;

        // Рисуем крутящуюся палочку в самом углу экрана
        Console.SetCursorPosition(79, 0);
        switch (_tick % 4)
        {
            case 0: Console.Write("-"); break;
            case 1: Console.Write("\\"); break;
            case 2: Console.Write("|"); break;
            case 3: Console.Write("/"); break;
        }

        Console.Write($"> ");
        var input = InputBus.ReadLine();

        Shell.RunCommand(input);
    }
}