using Microsoft.AspNetCore.DataProtection.Repositories;
using System.Xml.Linq;

namespace PgDataHub;

/// <summary>
/// Process-local Data Protection repository. PgDataHub does not use Data
/// Protection for its server-side admin sessions, but framework services may
/// initialize it. Keeping keys in memory avoids user-profile filesystem and
/// DPAPI dependencies for restricted service accounts.
/// </summary>
internal sealed class InMemoryXmlRepository : IXmlRepository
{
    private readonly Lock _gate = new();
    private readonly List<XElement> _elements = [];

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        lock (_gate) return _elements.Select(x => new XElement(x)).ToArray();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        lock (_gate) _elements.Add(new XElement(element));
    }
}
