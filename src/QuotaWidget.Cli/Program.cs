using System.Text;

namespace QuotaWidget.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        return QuotasCommand.Run(args, Console.Out, Console.Error);
    }
}
