using System;
using System.IO;

namespace MapFileDict
{
    internal class MemMapStream : Stream
    {
        public MapFileLocator _mfloc;
        public IntPtr _addrStart;
        int _position;
        public override string ToString()
        {
            return string.Format("{0} Addr={1:x0}, Pos={2}", _mfloc.ToString(), _addrStart.ToInt32(), _position);
        }
        public override bool CanRead
        {
            get { return true; }
        }

        public override bool CanSeek
        {
            get { return true; }
        }

        public override bool CanWrite
        {
            get { return true; }
        }

        public override void Flush()
        {
            throw new NotImplementedException();
        }

        public override long Length
        {
            get { return _mfloc.ulSize; }
        }

        public override long Position
        {
            get
            {
                return _position;
            }
            set
            {
                if (value < 0 || value >= Length)
                {
                    throw new ArgumentOutOfRangeException();
                }
                _position = (int)value;
            }
        }

        unsafe public override int Read(byte[] buffer, int offset, int count)
        {
            byte* pMapStream = (byte*)_addrStart.ToPointer();
            fixed (byte* pBuff = buffer)
            {
                var numToRead = (int)Math.Min(offset + count, _position + Length);
                NativeMethods.CopyMemory((IntPtr)pBuff, (IntPtr)pMapStream +_position+ offset, (uint)numToRead);
                //for (int i = 0; i < numToRead; i++)
                //{
                //    buffer[i] = pMapStream[_position + i + offset];
                //}
                _position += numToRead;
            }
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:
                    _position = (int)offset;
                    break;
                case SeekOrigin.End:
                    _position = (int)Math.Max(0, Length - offset);
                    break;
                case SeekOrigin.Current:
                    _position = (int)Math.Min(Length, _position + offset);
                    break;
            }
            return _position;
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        unsafe public override void Write(byte[] buffer, int offset, int count)
        {
            byte* pMapStream = (byte*)_addrStart.ToPointer();
            fixed (byte* pBuff = buffer)
            {
                var numToWrite = (int)Math.Min(offset + count, _position + Length);
             //   Buffer.BlockCopy(buffer, 0, (Array)pMapStream,0, numToWrite);

                NativeMethods.CopyMemory((IntPtr)pMapStream + _position + offset, (IntPtr)pBuff, (uint)numToWrite);
                //for (int i = 0; i < numToWrite; i++)
                //{
                //    pMapStream[_position + i + offset] = buffer[i];
                //    *pMapStream++ = pBuff[i];
                //}
                _position += numToWrite;
            }
        }
    }
}
