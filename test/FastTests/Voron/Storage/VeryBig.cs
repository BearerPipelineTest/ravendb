﻿using System;
using System.IO;
using Xunit;
using Voron;

namespace FastTests.Voron.Storage
{
    public class VeryBig : StorageTest
    {
        [Fact]
        public void CanGrowBeyondInitialSize()
        {
            using (var tx = Env.WriteTransaction())
            {
                tx.CreateTree("test");
                tx.Commit();
            }

            var buffer = new byte[1024 * 512];
            new Random().NextBytes(buffer);

            for (int i = 0; i < 20; i++)
            {
                using (var tx = Env.WriteTransaction())
                {
                    var tree = tx.CreateTree("test");
                    for (int j = 0; j < 12; j++)
                    {
                        tree.Add(string.Format("{0:000}-{1:000}", j, i), new MemoryStream(buffer));
                    }
                    tx.Commit();
                }
            }
        }

        [Fact]
        public void CanGrowBeyondInitialSize_Root()
        {
            var buffer = new byte[1024 * 512];
            new Random().NextBytes(buffer);

            for (int i = 0; i < 20; i++)
            {
                using (var tx = Env.WriteTransaction())
                {
                    var tree = tx.CreateTree("test");
                    for (int j = 0; j < 12; j++)
                    {
                        tree.Add(string.Format("{0:000}-{1:000}", j, i), new MemoryStream(buffer));
                    }
                    tx.Commit();
                }
            }
        }
        [Fact]
        public void CanGrowBeyondInitialSize_WithAnotherTree()
        {
            using (var tx = Env.WriteTransaction())
            {
                tx.CreateTree("test");
                tx.Commit();
            }
            var buffer = new byte[1024 * 512];
            new Random().NextBytes(buffer);

            for (int i = 0; i < 20; i++)
            {
                using (var tx = Env.WriteTransaction())
                {

                    var tree = tx.CreateTree("test");
                    for (int j = 0; j < 12; j++)
                    {
                        tree.Add(string.Format("{0:000}-{1:000}", j, i), new MemoryStream(buffer));
                    }
                    tx.Commit();
                }
            }
        }
    }
}
