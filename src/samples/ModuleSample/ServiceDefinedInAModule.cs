using System;
namespace ModuleSample
{
    public class ServiceDefinedInAModule : IDisposable
    {
        public void Dispose()
        {
            throw new NotImplementedException();
        }
    }
}