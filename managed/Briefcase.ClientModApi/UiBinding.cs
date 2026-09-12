using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;

namespace Briefcase.ClientModApi;

/// <summary>
/// Strongly typed value used by Briefcase controls. A binding reads the current
/// value, can optionally write it, and can notify retained UI backends without
/// exposing Avalonia types to a mod.
/// </summary>
public sealed class UiBinding<T>
{
    private readonly Func<T> _read;
    private readonly Action<T>? _write;
    private readonly Func<Action, IDisposable>? _subscribe;

    internal UiBinding(
        Func<T> read,
        Action<T>? write,
        Func<Action, IDisposable>? subscribe)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _write = write;
        _subscribe = subscribe;
    }

    public T Value => _read();
    public bool CanWrite => _write is not null;
    public bool CanNotify => _subscribe is not null;

    public void SetValue(T value)
    {
        if (_write is null)
            throw new InvalidOperationException("This UI binding is read-only.");
        _write(value);
    }

    /// <summary>
    /// Observes invalidation notifications. The callback may run on any thread;
    /// UI backends are responsible for dispatching it to their own thread.
    /// </summary>
    public IDisposable Subscribe(Action invalidated)
    {
        ArgumentNullException.ThrowIfNull(invalidated);
        return _subscribe?.Invoke(invalidated) ?? EmptySubscription.Instance;
    }

    internal static UiBinding<T> Create(
        Func<T> read,
        Action<T>? write = null,
        Func<Action, IDisposable>? subscribe = null) =>
        new(read, write, subscribe);

    private sealed class EmptySubscription : IDisposable
    {
        public static readonly EmptySubscription Instance = new();
        public void Dispose() { }
    }
}

/// <summary>Factories for strongly typed ViewModel and ConfigEntry bindings.</summary>
public static partial class Ui
{
    /// <summary>Creates a two-way binding to a writable ViewModel property.</summary>
    public static UiBinding<TValue> Bind<TViewModel, TValue>(
        TViewModel viewModel,
        Expression<Func<TViewModel, TValue>> property)
        where TViewModel : class, INotifyPropertyChanged
    {
        var propertyInfo = RequireDirectProperty(property);
        if (propertyInfo.SetMethod?.IsPublic != true)
            throw new ArgumentException(
                $"Property '{propertyInfo.Name}' must have a public setter.",
                nameof(property));

        var read = property.Compile();
        var value = Expression.Parameter(typeof(TValue), "value");
        var write = Expression.Lambda<Action<TViewModel, TValue>>(
            Expression.Assign(property.Body, value),
            property.Parameters[0], value).Compile();
        return UiBinding<TValue>.Create(
            () => read(viewModel),
            candidate => write(viewModel, candidate),
            invalidated => ObserveProperty(viewModel, propertyInfo.Name, invalidated));
    }

    /// <summary>Creates a read-only binding to a ViewModel property.</summary>
    public static UiBinding<TValue> Observe<TViewModel, TValue>(
        TViewModel viewModel,
        Expression<Func<TViewModel, TValue>> property)
        where TViewModel : class, INotifyPropertyChanged
    {
        var propertyInfo = RequireDirectProperty(property);
        var read = property.Compile();
        return UiBinding<TValue>.Create(
            () => read(viewModel),
            subscribe: invalidated =>
                ObserveProperty(viewModel, propertyInfo.Name, invalidated));
    }

    /// <summary>
    /// Binds directly to a persistent Briefcase setting. Changes made by the mod
    /// and changes made in the UI follow the same validation and saving path.
    /// </summary>
    public static UiBinding<T> Bind<T>(Briefcase.ModApi.ConfigEntry<T> entry)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(entry);
        return UiBinding<T>.Create(
            () => entry.Value,
            value => entry.Value = value,
            invalidated =>
            {
                void Changed(T _) => invalidated();
                entry.ValueChanged += Changed;
                return new DelegateSubscription(() => entry.ValueChanged -= Changed);
            });
    }

    private static PropertyInfo RequireDirectProperty<TViewModel, TValue>(
        Expression<Func<TViewModel, TValue>> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (expression.Body is not MemberExpression
            {
                Member: PropertyInfo property,
                Expression: ParameterExpression parameter
            } || parameter != expression.Parameters[0])
            throw new ArgumentException(
                "A UI binding must target one direct ViewModel property, for example x => x.Enabled.",
                nameof(expression));
        return property;
    }

    private static IDisposable ObserveProperty<TViewModel>(
        TViewModel viewModel,
        string propertyName,
        Action invalidated)
        where TViewModel : class, INotifyPropertyChanged
    {
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (string.IsNullOrEmpty(args.PropertyName) ||
                string.Equals(args.PropertyName, propertyName, StringComparison.Ordinal))
                invalidated();
        };
        viewModel.PropertyChanged += handler;
        return new DelegateSubscription(() => viewModel.PropertyChanged -= handler);
    }

    private sealed class DelegateSubscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}