namespace NadeLab.Plugin;

// Address only: the caller owns the schema type and must reacquire storage
// whenever an engine call can grow the vector. Never cache this address.
internal static class NativeStructVector
{
    internal static nint Element(int count,int index,int stride,Func<nint> storage)
    {
        if(count is <1 or >64 || index<0 || index>=count || stride is <1 or >4096)
            throw new InvalidOperationException("Unsupported view-correction vector bounds or schema size.");
        nint start=storage();
        if(start==0) throw new InvalidOperationException("View-correction vector has no storage.");
        return checked(start+checked(index*stride));
    }
}
