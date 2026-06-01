using System;
using System.Collections.Generic;

namespace BAZOS.Drivers
{
    public sealed class GroupInfo
    {
        public ushort Id { get; set; }
        public string Name { get; set; } = "";
    }

    public static class SecurityContext
    {
        private static readonly Dictionary<ushort, GroupInfo> _groups = new();
        private static ushort _currentGroupId;

        static SecurityContext()
        {
            // Default: OS group, current user SYSTEM in OS group.
            _groups[0] = new GroupInfo { Id = 0, Name = "OS" };
            _currentGroupId = 0;
        }

        public static ushort CurrentGroupId => _currentGroupId;

        public static string CurrentUserName => "SYSTEM";

        public static string CurrentGroupName
            => _groups.TryGetValue(_currentGroupId, out var g) ? g.Name : _currentGroupId.ToString();

        public static IEnumerable<GroupInfo> AllGroups => _groups.Values;

        public static bool TrySetCurrentGroup(string nameOrId)
        {
            if (string.IsNullOrWhiteSpace(nameOrId))
                return false;
            nameOrId = nameOrId.Trim();

            if (ushort.TryParse(nameOrId, out var id))
            {
                if (_groups.ContainsKey(id))
                {
                    _currentGroupId = id;
                    return true;
                }
                return false;
            }

            foreach (var g in _groups.Values)
            {
                if (string.Equals(g.Name, nameOrId, StringComparison.OrdinalIgnoreCase))
                {
                    _currentGroupId = g.Id;
                    return true;
                }
            }

            return false;
        }

        public static bool AddGroup(string name, out ushort id)
        {
            id = 0;
            if (string.IsNullOrWhiteSpace(name))
                return false;
            name = name.Trim();

            foreach (var g in _groups.Values)
            {
                if (string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            ushort next = 0;
            while (_groups.ContainsKey(next))
                next++;

            _groups[next] = new GroupInfo { Id = next, Name = name };
            id = next;
            return true;
        }

        public static bool RemoveGroup(string nameOrId)
        {
            if (string.IsNullOrWhiteSpace(nameOrId))
                return false;

            if (ushort.TryParse(nameOrId.Trim(), out var id))
            {
                if (id == 0)
                    return false;
                return _groups.Remove(id);
            }

            ushort found = 0xFFFF;
            foreach (var g in _groups.Values)
            {
                if (string.Equals(g.Name, nameOrId.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    found = g.Id;
                    break;
                }
            }
            if (found == 0xFFFF || found == 0)
                return false;
            return _groups.Remove(found);
        }
    }
}

