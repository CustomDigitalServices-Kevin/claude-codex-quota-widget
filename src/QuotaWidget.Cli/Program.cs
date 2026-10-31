using System.Text;

namespace QuotaWidget.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = new UTF8Encoding(false, true);
        return NativeClaudeCommands.Run(args, Console.In, Console.Out, Console.Error)
            ?? WidgetExitCommand.Run(args, Console.Error)
            ?? QuotasCommand.Run(args, Console.Out, Console.Error);
    }
}
