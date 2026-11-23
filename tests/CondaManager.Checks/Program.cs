using System.Reflection;
using System.Text.Json;

namespace CondaManager.Checks;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--echo-args")
        {
            Console.Write(JsonSerializer.Serialize(args.Skip(1)));
            return 0;
        }
        if (args.Length == 2 && args[0] == "--sleep")
        {
            await Task.Delay(int.Parse(args[1]));
            return 0;
        }
        if (args.Length == 2 && args[0] == "--exit")
        {
            Console.Error.Write("requested exit");
            return int.Parse(args[1]);
        }

        var methods = Assembly.GetExecutingAssembly().GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(method => method.GetCustomAttribute<CheckAttribute>() is not null)
            .OrderBy(method => method.DeclaringType?.Name)
            .ThenBy(method => method.Name)
            .ToArray();
        var failed = 0;
        foreach (var method in methods)
        {
            var label = method.GetCustomAttribute<CheckAttribute>()!.Name;
            try
            {
                var result = method.Invoke(null, null);
                if (result is Task task)
                    await task;
                Console.WriteLine($"PASS {label}");
            }
            catch (Exception exception)
            {
                failed++;
                var cause = exception is TargetInvocationException invocation
                    ? invocation.InnerException ?? exception
                    : exception;
                Console.Error.WriteLine($"FAIL {label}: {cause}");
            }
        }

        Console.WriteLine($"{methods.Length - failed}/{methods.Length} checks passed");
        return failed == 0 ? 0 : 1;
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class CheckAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

public static class Expect
{
    public static void True(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected {expected}; got {actual}");
    }

    public static TException Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }
        throw new Exception($"Expected {typeof(TException).Name}");
    }

    public static async Task<TException> ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }
        throw new Exception($"Expected {typeof(TException).Name}");
    }
}
