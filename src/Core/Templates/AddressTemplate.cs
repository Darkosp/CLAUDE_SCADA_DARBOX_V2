using System.Text;

namespace ScadaDarbox.Core.Templates;

/// <summary>
/// Resolves a template tag's address against one instance's parameters (ADR-0010).
/// </summary>
/// <remarks>
/// The substitution itself is protocol-agnostic — it replaces <c>{name}</c> with the
/// value that instance supplied for <c>name</c> — which is why it belongs in core. What
/// the resolved string then means is entirely the driver's business (ADR-0002): core
/// never learns that <c>holding:</c> is a Modbus area or that an offset is a register
/// number.
/// </remarks>
public static class AddressTemplate
{
    /// <summary>
    /// Substitutes every <c>{name}</c> placeholder with the matching parameter.
    /// </summary>
    /// <exception cref="TemplateParameterMissingException">
    /// The template names a parameter this instance did not supply. Resolving it to an
    /// empty string would produce an address that looks valid and reads the wrong place,
    /// so it fails instead.
    /// </exception>
    public static string Resolve(string addressTemplate, IReadOnlyDictionary<string, string> parameters)
    {
        var result = new StringBuilder(addressTemplate.Length);
        var index = 0;

        while (index < addressTemplate.Length)
        {
            var open = addressTemplate.IndexOf('{', index);

            if (open < 0)
            {
                result.Append(addressTemplate, index, addressTemplate.Length - index);
                break;
            }

            var close = addressTemplate.IndexOf('}', open);

            if (close < 0)
            {
                // An unmatched brace is literal text rather than a broken placeholder:
                // a driver's address syntax may legitimately contain one.
                result.Append(addressTemplate, index, addressTemplate.Length - index);
                break;
            }

            result.Append(addressTemplate, index, open - index);

            var name = addressTemplate[(open + 1)..close];

            if (!parameters.TryGetValue(name, out var value))
            {
                throw new TemplateParameterMissingException(name, addressTemplate);
            }

            result.Append(value);
            index = close + 1;
        }

        return result.ToString();
    }

    /// <summary>Every parameter name a template refers to, in order of first appearance.</summary>
    public static IReadOnlyList<string> ParameterNames(string addressTemplate)
    {
        var names = new List<string>();
        var index = 0;

        while (index < addressTemplate.Length)
        {
            var open = addressTemplate.IndexOf('{', index);
            if (open < 0)
            {
                break;
            }

            var close = addressTemplate.IndexOf('}', open);
            if (close < 0)
            {
                break;
            }

            var name = addressTemplate[(open + 1)..close];
            if (!names.Contains(name))
            {
                names.Add(name);
            }

            index = close + 1;
        }

        return names;
    }
}

/// <summary>
/// A template address named a parameter the instance being created does not have.
/// </summary>
public sealed class TemplateParameterMissingException : Exception
{
    public TemplateParameterMissingException(string parameterName, string addressTemplate)
        : base($"This device has no '{parameterName}' parameter, which its template's address '{addressTemplate}' needs.")
    {
        ParameterName = parameterName;
    }

    public string ParameterName { get; }
}
