using System;
using System.Collections.Generic;
using InvisibleGorillaXRay.Handlers.Tor;
using InvisibleGorillaXRay.Models;

namespace AuditHarness
{
    /// <summary>
    /// Небольшой набор проверок «чистой» логики Tor-слоя (без сети и процессов).
    /// Запуск: dotnet run --project tests\AuditHarness
    /// </summary>
    internal static class Program
    {
        private static int passed;
        private static int failed;

        private static int Main()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            Check("Build: WEBTUNNEL без строк мостов падает (fail-closed, а не direct)",
                () =>
                {
                    AssertThrows<InvalidOperationException>(() => TorrcBuilder.Build(
                        9250, 9251, @"C:\tmp\data", @"C:\tmp\data\cookie", @"C:\tmp\tor.log",
                        BridgeType.WEBTUNNEL, Array.Empty<string>()));
                });

            Check("Build: obfs4-строка попадает в torrc с UseBridges и префиксом",
                () =>
                {
                    string torrc = TorrcBuilder.Build(
                        9250, 9251, @"C:\tmp\data", @"C:\tmp\data\cookie", @"C:\tmp\tor.log",
                        BridgeType.OBFS4, new[] { "1.2.3.4:443 cert=abcdef" });
                    Assert(torrc.Contains("UseBridges 1"), "нет UseBridges 1");
                    Assert(torrc.Contains("Bridge obfs4 1.2.3.4:443 cert=abcdef"), "нет строки моста с префиксом obfs4");
                    Assert(torrc.Contains("ClientTransportPlugin obfs4 exec"), "нет ClientTransportPlugin obfs4");
                });

            Check("Build: NONE не добавляет UseBridges (прямое подключение разрешено)",
                () =>
                {
                    string torrc = TorrcBuilder.Build(
                        9250, 9251, @"C:\tmp\data", @"C:\tmp\data\cookie", @"C:\tmp\tor.log",
                        BridgeType.NONE, Array.Empty<string>());
                    Assert(!torrc.Contains("UseBridges"), "UseBridges не должен появляться для NONE");
                    Assert(!torrc.Contains("Bridge "), "не должно быть строк Bridge для NONE");
                });

            Check("TryGetRuntimeBridge: snowflake берёт встроенные мосты",
                () =>
                {
                    bool ok = TorrcBuilder.TryGetRuntimeBridge(
                        BridgeType.SNOWFLAKE, null, out string plugin, out List<string> bridges, out string error);
                    Assert(ok, "ожидался успех, ошибка: " + error);
                    Assert(plugin != null && plugin.StartsWith("snowflake exec"), "plugin: " + plugin);
                    Assert(bridges.Count > 0, "ожидались встроенные snowflake-мосты");
                });

            Check("TryGetRuntimeBridge: пустой список для obfs4 подставляет встроенные",
                () =>
                {
                    bool ok = TorrcBuilder.TryGetRuntimeBridge(
                        BridgeType.OBFS4, Array.Empty<string>(), out _, out List<string> bridges, out string error);
                    Assert(ok, "ожидался успех, ошибка: " + error);
                    Assert(bridges.Count > 0, "ожидались встроенные obfs4-мосты");
                });

            Check("QuoteConfValue: перевод строки не может инжектить команду в control-port",
                () =>
                {
                    string quoted = TorrcBuilder.QuoteConfValue("obfs4 1.2.3.4:443\r\nSETCONF UseBridges=0");
                    Assert(quoted.IndexOf('\r') < 0 && quoted.IndexOf('\n') < 0,
                        "в значении остались CR/LF: " + Escape(quoted));
                });

            Check("QuoteConfValue: кавычки и обратные слэши экранируются",
                () =>
                {
                    string quoted = TorrcBuilder.QuoteConfValue("a\"b\\c");
                    Assert(quoted == "\"a\\\"b\\\\c\"", "неожиданный результат: " + Escape(quoted));
                });

            Check("QuoteConfValue: пустое значение даёт пустую строку в кавычках",
                () =>
                {
                    Assert(TorrcBuilder.QuoteConfValue("") == "\"\"", "ожидалось \"\"");
                    Assert(TorrcBuilder.QuoteConfValue(null!) == "\"\"", "ожидалось \"\" для null");
                });

            Check("GetValidFileName: '..' не становится каталогом",
                () =>
                {
                    Assert(string.IsNullOrEmpty(InvisibleGorillaXRay.Utilities.FileUtility.GetValidFileName("..")),
                        "имя '..' должно быть отвергнуто");
                    Assert(string.IsNullOrEmpty(InvisibleGorillaXRay.Utilities.FileUtility.GetValidFileName("  .  ")),
                        "имя из точек должно быть отвергнуто");
                    Assert(InvisibleGorillaXRay.Utilities.FileUtility.GetValidFileName("CON") == "_CON",
                        "зарезервированное имя Windows должно получить префикс");
                });

            Check("Build: строки мостов с переводом строки не ломают torrc",
                () =>
                {
                    string torrc = TorrcBuilder.Build(
                        9250, 9251, @"C:\tmp\data", @"C:\tmp\data\cookie", @"C:\tmp\tor.log",
                        BridgeType.OBFS4, new[] { "1.2.3.4:443\nUseBridges 0" });
                    int useBridgesCount = CountOccurrences(torrc, "UseBridges");
                    Assert(useBridgesCount == 1, "инъекция: UseBridges встречается " + useBridgesCount + " раз");
                });

            Console.WriteLine();
            Console.WriteLine($"Итого: passed={passed}, failed={failed}");
            return failed == 0 ? 0 : 1;
        }

        private static void Check(string name, Action test)
        {
            try
            {
                test();
                passed++;
                Console.WriteLine("PASS  " + name);
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine("FAIL  " + name);
                Console.WriteLine("      " + ex.Message);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }

        private static void AssertThrows<T>(Action action) where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                return;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"ожидалось {typeof(T).Name}, получено {ex.GetType().Name}: {ex.Message}");
            }

            throw new InvalidOperationException($"ожидалось {typeof(T).Name}, исключения не было");
        }

        private static int CountOccurrences(string text, string needle)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

        private static string Escape(string value) => value?.Replace("\r", "\\r").Replace("\n", "\\n");
    }
}
