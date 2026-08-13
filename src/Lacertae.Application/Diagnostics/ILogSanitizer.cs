namespace Lacertae.Application.Diagnostics;

public interface ILogSanitizer
{
    string Sanitize(string value);
}
