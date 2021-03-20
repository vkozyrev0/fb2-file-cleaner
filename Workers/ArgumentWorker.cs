using System;
using Fb2CleanerApp.Enums;
using Microsoft.Extensions.CommandLineUtils;

namespace Fb2CleanerApp.Workers
{
    public static class ArgumentWorker
    {
        public static string Folder { get; set; }
        public static bool Recursive { get; set; }
        public static ActionType Action { get; set; }
        public static int Concurrency { get; set; }
        public static string EncodingCodePage { get; set; }

        public static void Parse(string[] args)
        {
            var app = new CommandLineApplication(throwOnUnexpectedArg: true)
            {
                Name = "Fb2 File Collection Fixed",
                Description = "This application detects invalid fb2 file and fixes those. It also find and removed duplicates or incomplete older files and removes them."
            };
            app.HelpOption("-?|-h|--help|-x");

            // Azure ADF and App Info

            var folder = app.Option("-f|--folder <Path>", "Points to the location where FB2 files are stored.", CommandOptionType.SingleValue);
            var recursive = app.Option("-r|--recursive <BooleanValue>", "(true|false) The <Path> folder with searched recursively.", CommandOptionType.SingleValue);
            var action = app.Option("-a|--action <Action>", "(Fix|Restore) Fixed the know issues or restores from the backup files.", CommandOptionType.SingleValue);
            var concurrency = app.Option("-c|--concurrency <IntegerValue>", "(1..200) Allows to process multiple files at the same time.", CommandOptionType.SingleValue);
            var encodingCodePage = app.Option("-e|--codepage <CodePage>", "(cp866) Allows to read file names in the zip archives correctly", CommandOptionType.SingleValue);

            app.OnExecute(() =>
            {
                if (folder.HasValue())
                {
                    Folder = folder.Value();
                }
                else
                {
                    Console.WriteLine("Folder parameter is not specified.");
                    throw new InvalidOperationException();
                }

                if (recursive.HasValue())
                {
                    Recursive = bool.Parse(recursive.Value());
                }
                else
                {
                    Console.WriteLine("Recursive parameter is not specified. Setting recursive to false.");
                    Recursive = false;
                }

                if (action.HasValue())
                {
                    Action = Enum.Parse<ActionType>(action.Value());
                }

                if (concurrency.HasValue())
                {
                    Concurrency = int.Parse(concurrency.Value());
                    if (Concurrency <= 0 || Concurrency > 200)
                    {
                        Console.WriteLine($"Concurrency value of {Concurrency} is invalid. Specify concurrency in 1 to 200 range.");
                        throw new InvalidOperationException();
                    }
                }
                else
                {
                    Console.WriteLine("Concurrency is not specified");
                    Concurrency = 32;
                }
                Console.WriteLine($"Concurrency value is set to {Concurrency}");

                if (encodingCodePage.HasValue())
                {
                    EncodingCodePage = encodingCodePage.Value();
                }
                else
                {
                    //ISO-8859-1?
                    Console.WriteLine("CodePage is set to cp866");
                    EncodingCodePage = "cp866";
                }
                return 0;
            });

            app.Execute(args);
        }
    }
}
