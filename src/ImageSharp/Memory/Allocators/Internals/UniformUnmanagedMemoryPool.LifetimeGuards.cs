﻿// Copyright (c) Six Labors.
// Licensed under the Apache License, Version 2.0.

namespace SixLabors.ImageSharp.Memory.Internals
{
    internal partial class UniformUnmanagedMemoryPool
    {
        public UnmanagedBuffer<T> CreateGuardedBuffer<T>(
            UnmanagedMemoryHandle handle,
            int lengthInElements,
            bool clear)
            where T : struct
        {
            var buffer = new UnmanagedBuffer<T>(lengthInElements, new ReturnToPoolBufferLifetimeGuard(this, handle));
            if (clear)
            {
                buffer.Clear();
            }

            return buffer;
        }

        public RefCountedLifetimeGuard CreateGroupLifetimeGuard(UnmanagedMemoryHandle[] handles) => new GroupLifetimeGuard(this, handles);

        private sealed class GroupLifetimeGuard : RefCountedLifetimeGuard
        {
            private readonly UniformUnmanagedMemoryPool pool;
            private readonly UnmanagedMemoryHandle[] handles;

            public GroupLifetimeGuard(UniformUnmanagedMemoryPool pool, UnmanagedMemoryHandle[] handles)
            {
                this.pool = pool;
                this.handles = handles;
            }

            protected override void Release()
            {
                if (!this.pool.Return(this.handles))
                {
                    foreach (UnmanagedMemoryHandle handle in this.handles)
                    {
                        handle.Free();
                    }
                }
            }
        }

        private sealed class ReturnToPoolBufferLifetimeGuard : UnmanagedBufferLifetimeGuard
        {
            private readonly UniformUnmanagedMemoryPool pool;

            public ReturnToPoolBufferLifetimeGuard(UniformUnmanagedMemoryPool pool, UnmanagedMemoryHandle handle)
                : base(handle) =>
                this.pool = pool;

            protected override void Release()
            {
                if (!this.pool.Return(this.Handle))
                {
                    this.Handle.Free();
                }
            }
        }
    }
}
