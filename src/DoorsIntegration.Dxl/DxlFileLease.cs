using System;
using System.IO;
using System.Threading;

namespace DoorsIntegration.Dxl
{
    // A snapshot owns the file; active readers may finish after snapshot disposal.
    internal sealed class DxlFileLease : IDisposable
    {
        private readonly object sync = new object();
        private readonly DxlExecutionResult result;
        private bool disposed;
        private int readers;
        public DxlFileLease(DxlExecutionResult result) { this.result = result; }
        public Stream Open()
        {
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException(nameof(DxlFileLease));
                var input = result.OpenResultStream(); readers++;
                return new Reader(input, Release);
            }
        }
        public void Dispose()
        {
            bool release;
            lock (sync) { if (disposed) return; disposed = true; release = readers == 0; }
            if (release) result.Dispose();
        }
        private void Release()
        {
            bool release;
            lock (sync) { release = --readers == 0 && disposed; }
            if (release) result.Dispose();
        }
        private sealed class Reader : Stream
        {
            private readonly Stream input;
            private Action release;
            public Reader(Stream input, Action release) { this.input = input; this.release = release; }
            public override bool CanRead => input.CanRead;
            public override bool CanSeek => input.CanSeek;
            public override bool CanWrite => false;
            public override long Length => input.Length;
            public override long Position { get => input.Position; set => input.Position = value; }
            public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => input.Seek(offset, origin);
            public override void Flush() { }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    var action = Interlocked.Exchange(ref release, null);
                    if (action != null) { try { input.Dispose(); } finally { action(); } }
                }
                base.Dispose(disposing);
            }
        }
    }
}
