// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.IO;

namespace System.Threading
{
    //
    // Implementation of ThreadPoolBoundHandle that sits on top of the Win32 ThreadPool
    //
    public sealed class ThreadPoolBoundHandle : IDisposable, IDeferredDisposable
    {
        private readonly SafeHandle _handle;
        private readonly SafeThreadPoolIOHandle _threadPoolHandle;
        private DeferredDisposableLifetime<ThreadPoolBoundHandle> _lifetime;

        private ThreadPoolBoundHandle(SafeHandle handle, SafeThreadPoolIOHandle threadPoolHandle)
        {
            _threadPoolHandle = threadPoolHandle;
            _handle = handle;
        }

        public SafeHandle Handle
        {
            get { return _handle; }
        }

        public static ThreadPoolBoundHandle BindHandle(SafeHandle handle)
        {
            if (handle == null)
                throw new ArgumentNullException(nameof(handle));

            if (handle.IsClosed || handle.IsInvalid)
                throw new ArgumentException(SR.Argument_InvalidHandle, nameof(handle));

            IntPtr callback = AddrofIntrinsics.AddrOf<Interop.NativeIoCompletionCallback>(OnNativeIOCompleted);
            SafeThreadPoolIOHandle threadPoolHandle = Interop.mincore.CreateThreadpoolIo(handle, callback, IntPtr.Zero, IntPtr.Zero);
            if (threadPoolHandle.IsInvalid)
            {
                int errorCode = Marshal.GetLastWin32Error();
                if (errorCode == Interop.Errors.ERROR_INVALID_HANDLE)         // Bad handle
                    throw new ArgumentException(SR.Argument_InvalidHandle, nameof(handle));

                if (errorCode == Interop.Errors.ERROR_INVALID_PARAMETER)     // Handle already bound or sync handle
                    throw new ArgumentException(SR.Argument_AlreadyBoundOrSyncHandle, nameof(handle));

                throw Win32Marshal.GetExceptionForWin32Error(errorCode);
            }

            return new ThreadPoolBoundHandle(handle, threadPoolHandle);
        }

        [CLSCompliant(false)]
        public unsafe NativeOverlapped* AllocateNativeOverlapped(IOCompletionCallback callback, object state, object pinData)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));

            AddRef();
            try
            {
                Win32ThreadPoolNativeOverlapped* overlapped = Win32ThreadPoolNativeOverlapped.Allocate(callback, state, pinData, preAllocated: null);
                overlapped->Data._boundHandle = this;

                Interop.mincore.StartThreadpoolIo(_threadPoolHandle);

                return Win32ThreadPoolNativeOverlapped.ToNativeOverlapped(overlapped);
            }
            catch
            {
                Release();
                throw;
            }
        }

        [CLSCompliant(false)]
        public unsafe NativeOverlapped* AllocateNativeOverlapped(PreAllocatedOverlapped preAllocated)
        {
            if (preAllocated == null)
                throw new ArgumentNullException(nameof(preAllocated));

            bool addedRefToThis = false;
            bool addedRefToPreAllocated = false;
            try
            {
                addedRefToThis = AddRef();
                addedRefToPreAllocated = preAllocated.AddRef();

                Win32ThreadPoolNativeOverlapped.OverlappedData data = preAllocated._overlapped->Data;
                if (data._boundHandle != null)
                    throw new ArgumentException(SR.Argument_PreAllocatedAlreadyAllocated, nameof(preAllocated));

                data._boundHandle = this;

                Interop.mincore.StartThreadpoolIo(_threadPoolHandle);

                return Win32ThreadPoolNativeOverlapped.ToNativeOverlapped(preAllocated._overlapped);
            }
            catch
            {
                if (addedRefToPreAllocated)
                    preAllocated.Release();
                if (addedRefToThis)
                    Release();
                throw;
            }
        }

        [CLSCompliant(false)]
        public unsafe void FreeNativeOverlapped(NativeOverlapped* overlapped)
        {
            if (overlapped == null)
                throw new ArgumentNullException(nameof(overlapped));

            Win32ThreadPoolNativeOverlapped* threadPoolOverlapped = Win32ThreadPoolNativeOverlapped.FromNativeOverlapped(overlapped);
            Win32ThreadPoolNativeOverlapped.OverlappedData data = GetOverlappedData(threadPoolOverlapped, this);

            if (!data._completed)
            {
                Interop.mincore.CancelThreadpoolIo(_threadPoolHandle);
                Release();
            }

            data._boundHandle = null;
            data._completed = false;

            if (data._preAllocated != null)
                data._preAllocated.Release();
            else
                Win32ThreadPoolNativeOverlapped.Free(threadPoolOverlapped);
        }

        [CLSCompliant(false)]
        public static unsafe object GetNativeOverlappedState(NativeOverlapped* overlapped)
        {
            if (overlapped == null)
                throw new ArgumentNullException(nameof(overlapped));

            Win32ThreadPoolNativeOverlapped* threadPoolOverlapped = Win32ThreadPoolNativeOverlapped.FromNativeOverlapped(overlapped);
            Win32ThreadPoolNativeOverlapped.OverlappedData data = GetOverlappedData(threadPoolOverlapped, null);

            return data._state;
        }

        private static unsafe Win32ThreadPoolNativeOverlapped.OverlappedData GetOverlappedData(Win32ThreadPoolNativeOverlapped* overlapped, ThreadPoolBoundHandle expectedBoundHandle)
        {
            Win32ThreadPoolNativeOverlapped.OverlappedData data = overlapped->Data;

            if (data._boundHandle == null)
                throw new ArgumentException(SR.Argument_NativeOverlappedAlreadyFree, nameof(overlapped));

            if (expectedBoundHandle != null && data._boundHandle != expectedBoundHandle)
                throw new ArgumentException(SR.Argument_NativeOverlappedWrongBoundHandle, nameof(overlapped));

            return data;
        }

        [NativeCallable(CallingConvention = CallingConvention.StdCall)]
        private static unsafe void OnNativeIOCompleted(IntPtr instance, IntPtr context, IntPtr overlappedPtr, uint ioResult, UIntPtr numberOfBytesTransferred, IntPtr ioPtr)
        {
            var wrapper = ThreadPoolCallbackWrapper.Enter();
            Win32ThreadPoolNativeOverlapped* overlapped = (Win32ThreadPoolNativeOverlapped*)overlappedPtr;

            ThreadPoolBoundHandle boundHandle = overlapped->Data._boundHandle;
            if (boundHandle == null)
                throw new InvalidOperationException(SR.Argument_NativeOverlappedAlreadyFree);

            boundHandle.Release();

            Win32ThreadPoolNativeOverlapped.CompleteWithCallback(ioResult, (uint)numberOfBytesTransferred, overlapped);
            wrapper.Exit();
        }

        private bool AddRef()
        {
            return _lifetime.AddRef(this);
        }

        private void Release()
        {
            _lifetime.Release(this);
        }

        public void Dispose()
        {
            _lifetime.Dispose(this);
            GC.SuppressFinalize(this);
        }

        ~ThreadPoolBoundHandle()
        {
            //
            // During shutdown, don't automatically clean up, because this instance may still be
            // reachable/usable by other code.
            //
            if (!Environment.HasShutdownStarted)
                Dispose();
        }

        void IDeferredDisposable.OnFinalRelease(bool disposed)
        {
            if (disposed)
                _threadPoolHandle.Dispose();
        }
    }
}
