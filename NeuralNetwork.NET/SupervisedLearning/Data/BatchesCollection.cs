﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using JetBrains.Annotations;
using NeuralNetworkNET.APIs.Interfaces;
using NeuralNetworkNET.APIs.Structs;
using NeuralNetworkNET.Extensions;
using NeuralNetworkNET.Helpers;

namespace NeuralNetworkNET.SupervisedLearning.Data
{
    /// <summary>
    /// A class that represents a set of samples batches to be used in circular order
    /// </summary>
    internal sealed class BatchesCollection : IDataset
    {
        /// <summary>
        /// Gets the collection of samples batches to use
        /// </summary>
        [NotNull]
        public SamplesBatch[] Batches { get; private set; }

        #region Interface

        /// <inheritdoc/>
        public int Count { get; }

        /// <inheritdoc/>
        public int InputFeatures => Batches[0].X.GetLength(1);

        /// <inheritdoc/>
        public int OutputFeatures => Batches[0].Y.GetLength(1);

        /// <inheritdoc/>
        public DatasetSample this[int i]
        {
            get
            {
                if (i < 0 || i > Count - 1) throw new ArgumentOutOfRangeException(nameof(i), "The target index is not valid");
                ref readonly SamplesBatch batch = ref Batches[i / Batches.Length];
                return new DatasetSample(batch.X.Slice(i), batch.Y.Slice(i));
            }
        }

        /// <inheritdoc/>
        public int BatchSize
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Batches.Length;
            set
            {
                // Setup
                if (value < 10) throw new ArgumentOutOfRangeException(nameof(BatchSize), "The batch size must be greater than or equal to 10");
                Reshape(value);
            }
        }

        /// <inheritdoc/>
        public long ByteSize => sizeof(float) * Count * (this[0].X.Length + this[0].Y.Length);

        #endregion

        #region Initialization

        // Private constructor from a given collection
        private BatchesCollection([NotNull] SamplesBatch[] batches)
        {
            Batches = batches;
            Count = batches.Sum(b => b.X.GetLength(0));
        }

        /// <summary>
        /// Creates a series of batches from the input dataset and expected results
        /// </summary>
        /// <param name="dataset">The source dataset to create the batches</param>
        /// <param name="size">The desired batch size</param>
        /// <exception cref="ArgumentOutOfRangeException">The dataset and result matrices have a different number of rows</exception>
        [NotNull]
        [CollectionAccess(CollectionAccessType.Read)]
        public static BatchesCollection From((float[,] X, float[,] Y) dataset, int size)
        {
            // Local parameters
            if (size < 10) throw new ArgumentOutOfRangeException(nameof(size), "The batch size can't be smaller than 10");
            int
                samples = dataset.X.GetLength(0),
                wx = dataset.X.GetLength(1),
                wy = dataset.Y.GetLength(1);
            if (samples != dataset.Y.GetLength(0)) throw new ArgumentOutOfRangeException(nameof(dataset), "The number of samples must be the same in both x and y");

            // Prepare the different batches
            int
                nBatches = samples / size,
                nBatchMod = samples % size;
            bool oddBatchPresent = nBatchMod > 0;
            SamplesBatch[] batches = new SamplesBatch[nBatches + (oddBatchPresent ? 1 : 0)];
            for (int i = 0; i < batches.Length; i++)
            {
                if (oddBatchPresent && i == batches.Length - 1)
                {
                    batches[i] = SamplesBatch.From(
                        Span<float>.DangerousCreate(dataset.X, ref dataset.X[i * size, 0], nBatchMod * wx),
                        Span<float>.DangerousCreate(dataset.Y, ref dataset.Y[i * size, 0], nBatchMod * wy),
                        wx, wy);
                }
                else
                {
                    batches[i] = SamplesBatch.From(
                        Span<float>.DangerousCreate(dataset.X, ref dataset.X[i * size, 0], size * wx),
                        Span<float>.DangerousCreate(dataset.Y, ref dataset.Y[i * size, 0], size * wy),
                        wx, wy);
                }
            }
            return new BatchesCollection(batches);
        }

        /// <summary>
        /// Creates a series of batches from the input dataset and expected results
        /// </summary>
        /// <param name="dataset">The source dataset to create the batches</param>
        /// <param name="size">The desired batch size</param>
        /// <exception cref="ArgumentOutOfRangeException">The dataset and result matrices have a different number of rows</exception>
        [NotNull]
        [CollectionAccess(CollectionAccessType.Read)]
        public static BatchesCollection From([NotNull] IEnumerable<Func<(float[] X, float[] Y)>> dataset, int size)
        {
            // Local parameters
            if (size < 10) throw new ArgumentOutOfRangeException(nameof(size), "The batch size can't be smaller than 10");
            return new BatchesCollection(dataset.AsParallel().Select(f => f()).Partition(size).Select(SamplesBatch.From).ToArray());
        }

        /// <summary>
        /// Creates a series of batches from the input dataset and expected results
        /// </summary>
        /// <param name="dataset">The source dataset to create the batches</param>
        /// <param name="size">The desired batch size</param>
        /// <exception cref="ArgumentOutOfRangeException">The dataset and result matrices have a different number of rows</exception>
        [NotNull]
        [CollectionAccess(CollectionAccessType.Read)]
        public static BatchesCollection From([NotNull] IEnumerable<(float[] X, float[] Y)> dataset, int size)
        {
            // Local parameters
            if (size < 10) throw new ArgumentOutOfRangeException(nameof(size), "The batch size can't be smaller than 10");
            return new BatchesCollection(dataset.ToArray().AsParallel().Partition(size).Select(SamplesBatch.From).ToArray());
        }

        #endregion

        #region Misc

        // Reshapes the current dataset with the given batch size
        private unsafe void Reshape(int size)
        {
            // Pin the dataset
            GCHandle*
                xhandles = stackalloc GCHandle[Batches.Length],
                yhandles = stackalloc GCHandle[Batches.Length];
            for (int i = 0; i < Batches.Length; i++)
            {
                xhandles[i] = GCHandle.Alloc(Batches[i].X, GCHandleType.Pinned);
                yhandles[i] = GCHandle.Alloc(Batches[i].Y, GCHandleType.Pinned);
            }

            // Re-partition the current samples
            IEnumerable<SamplesBatch> query =
                from seq in Batches.AsParallel().SelectMany(batch =>
                    from i in Enumerable.Range(0, batch.X.GetLength(0))
                    select (Pin<float>.From(ref batch.X[i, 0]), Pin<float>.From(ref batch.Y[i, 0]))).Partition(size)
                select SamplesBatch.From(seq, InputFeatures, OutputFeatures);
            Batches = query.ToArray();

            // Cleanup
            for (int i = 0; i < Batches.Length; i++)
            {
                xhandles[i].Free();
                yhandles[i].Free();
            }
        }

        #endregion

        #region Shuffling

        /// <summary>
        /// Cross-shuffles the current dataset (shuffles samples in each batch, then shuffles the batches list)
        /// </summary>
        public unsafe void CrossShuffle()
        {
            // Select the couples to cross-shuffle
            int* indexes = stackalloc int[Batches.Length];
            for (int i = 0; i < Batches.Length; i++) indexes[i] = i;
            int n = Batches.Length;
            while (n > 1)
            {
                int k = ThreadSafeRandom.NextInt(max: n);
                n--;
                int value = indexes[k];
                indexes[k] = indexes[n];
                indexes[n] = value;
            }

            // Cross-shuffle the pairs of lists in parallel
            void Kernel(int i)
            {
                int a = indexes[i * 2], b = indexes[i * 2 + 1];
                SamplesBatch setA = Batches[a], setB = Batches[b];
                int
                    hA = setA.X.GetLength(0),
                    wx = setA.X.GetLength(1),
                    wy = setA.Y.GetLength(1),
                    hB = setB.X.GetLength(0),
                    bound = hA > hB ? hB : hA;
                float[]
                    tempX = new float[wx],
                    tempY = new float[wy];
                while (bound > 1)
                {
                    int k = ThreadSafeRandom.NextInt(max: bound);
                    bound--;
                    SamplesBatch
                        targetA = ThreadSafeRandom.NextBool() ? setA : setB,
                        targetB = ThreadSafeRandom.NextBool() ? setA : setB;

                    // Rows from A[k] to temp
                    Buffer.BlockCopy(targetA.X, sizeof(float) * wx * k, tempX, 0, sizeof(float) * wx);
                    Buffer.BlockCopy(targetA.Y, sizeof(float) * wy * k, tempY, 0, sizeof(float) * wy);

                    // Rows from B[bound] to A[k]
                    Buffer.BlockCopy(targetB.X, sizeof(float) * wx * bound, targetA.X, sizeof(float) * wx * k, sizeof(float) * wx);
                    Buffer.BlockCopy(targetB.Y, sizeof(float) * wy * bound, targetA.Y, sizeof(float) * wy * k, sizeof(float) * wy);

                    // Rows from temp to B[bound]
                    Buffer.BlockCopy(tempX, 0, targetB.X, sizeof(float) * wx * bound, sizeof(float) * wx);
                    Buffer.BlockCopy(tempY, 0, targetB.Y, sizeof(float) * wy * bound, sizeof(float) * wy);
                }
            }
            Parallel.For(0, Batches.Length / 2, Kernel).AssertCompleted();

            // Shuffle the main list
            n = Batches.Length;
            while (n > 1)
            {
                int k = ThreadSafeRandom.NextInt(max: n);
                n--;
                SamplesBatch value = Batches[k];
                Batches[k] = Batches[n];
                Batches[n] = value;
            }
        }

        #endregion
    }
}
