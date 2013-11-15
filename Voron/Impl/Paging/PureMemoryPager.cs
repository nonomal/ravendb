﻿using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Voron.Trees;

namespace Voron.Impl
{
	public unsafe class PureMemoryPager : AbstractPager
	{
		private IntPtr _ptr;
		private byte* _base;

		public PureMemoryPager(byte[] data)
		{
			_ptr = Marshal.AllocHGlobal(data.Length);
			_base = (byte*)_ptr.ToPointer();
			AllocatedSize = data.Length;
			NumberOfAllocatedPages = AllocatedSize / PageSize;
			PagerState.Release();
			PagerState = new PagerState
			{
				Ptr = _ptr,
                Base = _base
			};
			PagerState.AddRef();
			fixed (byte* origin = data)
			{
				NativeMethods.memcpy(_base, origin, data.Length);
			}
		}

		public PureMemoryPager()
		{
			_ptr = Marshal.AllocHGlobal(MinIncreaseSize);
			_base = (byte*)_ptr.ToPointer();
			AllocatedSize = 0;
			NumberOfAllocatedPages = 0;
			PagerState.Release();
			PagerState = new PagerState
				{
					Ptr = _ptr,
					Base = _base
				};
			PagerState.AddRef();
		}

	    public override void Write(Page page, long? pageNumber)
	    {
			var toWrite = page.IsOverflow ? GetNumberOfOverflowPages(page.OverflowSize): 1;
	        var requestedPageNumber = pageNumber ?? page.PageNumber;
            
            WriteDirect(page, requestedPageNumber, toWrite);
	    }

	    public override string ToString()
	    {
	        return "memory";
	    }

	    public override void WriteDirect(Page start, long pagePosition, int pagesToWrite)
	    {
            EnsureContinuous(null, pagePosition, pagesToWrite);
            NativeMethods.memcpy(AcquirePagePointer(pagePosition), start.Base, pagesToWrite * PageSize);
	    }

	    public override void Dispose()
		{
			base.Dispose();
			PagerState.Release();
			_base = null;
		}

		public override void Sync()
		{
			// nothing to do here
		}

		public override byte* AcquirePagePointer(long pageNumber)
		{
			return _base + (pageNumber * PageSize);
		}

		public override void AllocateMorePages(Transaction tx, long newLength)
		{
			if (newLength < AllocatedSize)
				throw new ArgumentException("Cannot set the legnth to less than the current length");
		    if (newLength == AllocatedSize)
		        return; // nothing to do

			var oldSize = AllocatedSize;
			AllocatedSize = newLength;
			NumberOfAllocatedPages = AllocatedSize / PageSize;
			var newPtr = Marshal.AllocHGlobal(new IntPtr(AllocatedSize));
			var newBase = (byte*)newPtr.ToPointer();
			NativeMethods.memcpy(newBase, _base, new IntPtr(oldSize));
			_base = newBase;
			_ptr = newPtr;


			var newPager = new PagerState { Ptr = newPtr, Base = _base };
			newPager.AddRef(); // one for the pager

			if (tx != null) // we only pass null during startup, and we don't need it there
			{
				newPager.AddRef(); // one for the current transaction
				tx.AddPagerState(newPager);
			}

			PagerState = newPager;
		}
	}
}