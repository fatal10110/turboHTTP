using System;
using System.Threading.Tasks;

public static class AssertAsync
{
    public static T ThrowsAsync<T>(Func<Task> asyncDelegate) where T : Exception
    {
        if (asyncDelegate == null)
            throw new ArgumentNullException(nameof(asyncDelegate));

        try
        {
            asyncDelegate().GetAwaiter().GetResult();
        }
        catch (T expected)
        {
            return expected;
        }
        catch (Exception ex)
        {
            throw new Exception($"Expected exception of type {typeof(T).Name}, but got {ex.GetType().Name}.", ex);
        }

        throw new Exception($"Expected exception of type {typeof(T).Name}, but no exception was thrown.");
    }
}
