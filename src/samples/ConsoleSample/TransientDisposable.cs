using System;
using System.Threading.Tasks;
namespace ConsoleSample;

public class TransientDisposable : IDisposable, IAsyncDisposable
{

    public void Dispose()
    {
        throw new NotImplementedException();
    }
    public ValueTask DisposeAsync() => throw new NotImplementedException();
}