﻿using System;
using System.Collections.Generic;
using System.Linq;
using Voron;
using Voron.Platform.Posix;
using Voron.Platform.Win32;
using Xunit;

namespace SlowTests.Voron
{
    public unsafe class MemoryMapWithoutBackingPagerTest : StorageTest
    {
        private readonly string dummyData;
        private const string LoremIpsum = "Lorem ipsum dolor sit amet, consectetur adipisicing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat. Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur. Excepteur sint occaecat cupidatat non proident, sunt in culpa qui officia deserunt mollit anim id est laborum.";
        private const string TestTreeName = "tree";
        private const long PagerInitialSize = 64 * 1024;
        public MemoryMapWithoutBackingPagerTest()
            : base(StorageEnvironmentOptions.CreateMemoryOnly())
        {
            dummyData = GenerateLoremIpsum(1024);
        }

        private string GenerateLoremIpsum(int count)
        {
            return String.Join(Environment.NewLine, Enumerable.Repeat(LoremIpsum, count));
        }

        private IEnumerable<KeyValuePair<string, string>> GenerateTestData()
        {
            for (int i = 0; i < 1000; i++)
                yield return new KeyValuePair<string, string>("Key " + i, "Data:" + dummyData);
        }

        [Theory]
        [InlineData(2)]
        [InlineData(5)]
        [InlineData(15)]
        [InlineData(100)]
        [InlineData(250)]
        public void Should_be_able_to_allocate_new_pages_with_apply_logs_to_data_file(int growthMultiplier)
        {
            _options.ManualFlushing = true;
             Env.Options.DataPager.EnsureContinuous(0, growthMultiplier);
            var testData = GenerateTestData().ToList();
            CreatTestSchema();
            using (var tx = Env.WriteTransaction())
            {
                var tree = tx.ReadTree(TestTreeName);
                foreach (var dataPair in testData)
                    tree.Add(dataPair.Key, StreamFor(dataPair.Value));

                tx.Commit();
            }
            Env.FlushLogToDataFile();
        }


        private void CreatTestSchema()
        {
            using (var tx = Env.WriteTransaction())
            {
                tx.CreateTree(TestTreeName);
                tx.Commit();
            }
        }

        [Fact]
        public void Should_be_able_to_allocate_new_pages_multiple_times()
        {
            var numberOfPages = PagerInitialSize / Env.Options.PageSize;
            for (int allocateMorePagesCount = 0; allocateMorePagesCount < 5; allocateMorePagesCount++)
            {
                numberOfPages *= 2;
                Env.Options.DataPager.EnsureContinuous(0, (int)(numberOfPages));
            }
        }

        byte* AllocateMemoryAtEndOfPager(long totalAllocationSize)
        {
            if (StorageEnvironmentOptions.RunningOnPosix)
            {
                var p = Syscall.mmap(new IntPtr(Env.Options.DataPager.PagerState.MapBase + totalAllocationSize), 16,
                    MmapProts.PROT_READ | MmapProts.PROT_WRITE, MmapFlags.MAP_ANONYMOUS, -1, 0);
                if (p.ToInt64() == -1)
                {
                    return null;
                }
                return (byte*)p.ToPointer();
            }
            return Win32NativeMethods.VirtualAlloc(Env.Options.DataPager.PagerState.MapBase + totalAllocationSize, new UIntPtr(16),
                Win32NativeMethods.AllocationType.RESERVE, Win32NativeMethods.MemoryProtection.EXECUTE_READWRITE);
        }

    }
}
