﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;

namespace Mygod.WifiShare
{
    // ReSharper disable MemberCanBePrivate.Global
    static class Arp
    {
        [DllImport("iphlpapi.dll")]
        private static extern int GetIpNetTable(IntPtr pIpNetTable, ref int pdwSize, bool bOrder);
        [DllImport("iphlpapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int FreeMibTable(IntPtr pIpNetTable);

        [StructLayout(LayoutKind.Sequential)]
        public struct MibIpNetRow
        {
            /// <summary>
            /// The index of the adapter.
            /// </summary>
            public readonly int Index;
            /// <summary>
            /// The length, in bytes, of the physical address.
            /// </summary>
            public readonly int PhysAddrLen;
            /// <summary>
            /// The physical address.
            /// </summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public readonly byte[] PhysAddr;
            /// <summary>
            /// The IPv4 address.
            /// </summary>
            public readonly uint Addr;
            /// <summary>
            /// The type of ARP entry. This type can be one of the following values.
            /// </summary>
            public readonly IPNetRowType Type;

            public string MacAddress => string.Join(":", PhysAddr.Take(PhysAddrLen).Select(b => b.ToString("X2")));
            public IPAddress IPAddress => new IPAddress(Addr);

            public override string ToString()
            {
                var type = string.Empty;
                switch (Type)
                {
                    case IPNetRowType.Other:
                        type = "其他";
                        break;
                    case IPNetRowType.Invalid:
                        type = "无效";
                        break;
                    case IPNetRowType.Dynamic:
                        type = "动态";
                        break;
                    case IPNetRowType.Static:
                        type = "静态";
                        break;
                }
                return string.IsNullOrWhiteSpace(type) ? IPAddress.ToString() : $"{IPAddress} ({type})";
            }
        }

        public enum IPNetRowType
        {
            Other = 1, Invalid, Dynamic, Static
        }

        private static readonly int MibIpNetRowSize = Marshal.SizeOf(typeof(MibIpNetRow));
        public static IEnumerable<MibIpNetRow> GetIpNetTable()
        {
            int bytesNeeded = 0, result = GetIpNetTable(IntPtr.Zero, ref bytesNeeded, false);
            if (result != 122) Helper.ThrowExceptionForHR(result);
            var buffer = IntPtr.Zero;
            try
            {
                buffer = Marshal.AllocCoTaskMem(bytesNeeded);
                Helper.ThrowExceptionForHR(GetIpNetTable(buffer, ref bytesNeeded, false));
                var entries = Marshal.ReadInt32(buffer);
                var ptr = buffer + 4;
                for (var index = 0; index < entries; index++, ptr += MibIpNetRowSize)
                    yield return Marshal.PtrToStructure<MibIpNetRow>(ptr);
            }
            finally
            {
                FreeMibTable(buffer);
            }
        }
    }
    // ReSharper restore MemberCanBePrivate.Global

    sealed class DnsCacheEntry
    {
        public DnsCacheEntry(IPAddress ip)
        {
            IPAddress = ip;
        }

        public void Update()
        {
            if (semaphore.Wait(0))
            {
                try
                {
                    var entry = Dns.GetHostEntry(IPAddress);
                    Domains = string.Join(", ", new[] { entry.HostName }.Union(entry.Aliases));
                }
                catch
                {
                    Domains = null;
                }
                cacheTime = DateTime.Now;
            }
            else semaphore.Wait();          // wait for the already running thread
            semaphore.Release();
        }
        public async void UpdateAsync()
        {
            if (!semaphore.Wait(0)) return; // already running
            try
            {
                var entry = await Dns.GetHostEntryAsync(IPAddress);
                Domains = string.Join(", ", new[] { entry.HostName }.Union(entry.Aliases));
            }
            catch
            {
                Domains = null;
            }
            cacheTime = DateTime.Now;
            semaphore.Release();
        }

        public readonly IPAddress IPAddress;
        public string Domains = "加载中";
        private DateTime cacheTime = DateTime.MinValue;
        private readonly SemaphoreSlim semaphore = new SemaphoreSlim(1);

        public bool Decayed => (DateTime.Now - cacheTime).TotalSeconds >= Program.Ttl;
    }
    sealed class DnsCache : KeyedCollection<IPAddress, DnsCacheEntry>
    {
        public string GetDomains(IPAddress ip, bool wait = false)
        {
            DnsCacheEntry entry;
            lock (this)
                if (Contains(ip)) entry = this[ip];
                else Add(entry = new DnsCacheEntry(ip));
            if (entry.Decayed)
                if (wait) entry.Update();
                else entry.UpdateAsync();
            return entry.Domains;
        }

        protected override IPAddress GetKeyForItem(DnsCacheEntry item)
        {
            return item.IPAddress;
        }
    }
}
