using System.Collections.Generic;
using BAZOS.Runtime;

namespace BAZOS.Core
{
    public static class Scheduler
    {
        private static readonly List<VmProcess> _processes = new List<VmProcess>();
        private static int _nextPid = 1;

        // Сколько инструкций VM может выполнить за один "тик" (квант времени)
        private const int TimeSliceInstructions = 50;

        public static int StartProcess(string name, VmModule module)
        {
            var proc = new VmProcess(_nextPid++, name, module);
            _processes.Add(proc);
            return proc.ProcessId;
        }

        public static void Tick()
        {
            if (_processes.Count == 0) return;

            // Проходим по всем процессам
            for (int i = 0; i < _processes.Count; i++)
            {
                var proc = _processes[i];

                if (!proc.IsFinished)
                {
                    proc.Step(TimeSliceInstructions);
                }
            }
            for (int i = _processes.Count - 1; i >= 0; i--)
            {
                if (_processes[i].IsFinished)
                {
                    _processes.RemoveAt(i);
                }
            }
        }

        public static VmProcess GetProcess(int pid)
        {
            for (int i = 0; i < _processes.Count; i++)
            {
                if (_processes[i].ProcessId == pid)
                    return _processes[i];
            }
            return null;
        }
    }
}