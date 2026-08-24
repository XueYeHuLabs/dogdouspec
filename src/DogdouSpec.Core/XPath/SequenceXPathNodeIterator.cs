using System.Xml.XPath;

namespace DogdouSpec.Core.XPath;

/// <summary>
/// An XPathNodeIterator representing an in-memory sequence of XPathNavigators.
/// Clones navigators on Current access to preserve stateful cursor isolation.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1010:Collections should implement generic interface", Justification = "Inherits from Framework XPathNodeIterator")]
public sealed class SequenceXPathNodeIterator : XPathNodeIterator
{
    private readonly IReadOnlyList<XPathNavigator> _navigators;
    private int _position;

    public SequenceXPathNodeIterator(IReadOnlyList<XPathNavigator> navigators)
    {
        _navigators = navigators ?? Array.Empty<XPathNavigator>();
        _position = 0;
    }

    private SequenceXPathNodeIterator(IReadOnlyList<XPathNavigator> navigators, int position)
    {
        _navigators = navigators;
        _position = position;
    }

    public override XPathNodeIterator Clone() =>
        new SequenceXPathNodeIterator(_navigators, _position);

    public override bool MoveNext()
    {
        if (_position < _navigators.Count)
        {
            _position++;
            return true;
        }
        return false;
    }

    public override XPathNavigator? Current =>
        _position > 0 && _position <= _navigators.Count
            ? _navigators[_position - 1].Clone()
            : null;

    public override int CurrentPosition => _position;

    public override int Count => _navigators.Count;
}
