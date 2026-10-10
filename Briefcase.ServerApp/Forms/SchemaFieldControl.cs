using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Briefcase.ServerApp;

/// <summary>A reusable editor for one field declared by a JSON form schema.</summary>
public sealed class SchemaFieldControl : Grid
{
    private sealed record ChoiceOption(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly FormField _field;
    private readonly Control _input;
    private readonly IconButton _reset;
    private readonly TextBlock? _sliderValue;
    private bool _settingValue;

    public event EventHandler? ValueChanged;
    public FormField Field => _field;

    public string Value => _input switch
    {
        CheckBox box => box.IsChecked == true ? "True" : "False",
        ComboBox combo => (combo.SelectedItem as ChoiceOption)?.Value ?? "",
        Slider slider => _field.FormatSliderValue(slider.Value),
        NumericUpDown number => number.Value?.ToString(CultureInfo.InvariantCulture) ?? "",
        TextBox text => text.Text ?? "",
        _ => ""
    };

    public SchemaFieldControl(FormSchema schema, FormField field, string value,
        IReadOnlyDictionary<string, string> values)
    {
        _field = field;
        ColumnDefinitions = new ColumnDefinitions("3*,2*,Auto");
        ColumnSpacing = 10;

        Children.Add(new TextBlock
        {
            Text = schema.Text(field.LabelKey, field.Label),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });

        Control display;
        if (field.Kind == FormFieldKind.Boolean)
        {
            var box = new CheckBox { IsChecked = value.Equals("True", StringComparison.OrdinalIgnoreCase) || value == "1" };
            box.PropertyChanged += (_, change) =>
            {
                if (change.Property == ToggleButton.IsCheckedProperty) InputChanged();
            };
            _input = display = box;
        }
        else if (field.Kind == FormFieldKind.Choice)
        {
            var options = field.Choices!.Select(choice => new ChoiceOption(choice.Value,
                schema.Text(choice.LabelKey, choice.Label))).ToArray();
            var combo = new ComboBox
            {
                ItemsSource = options,
                SelectedItem = options.FirstOrDefault(option => option.Value == value)
            };
            combo.SelectionChanged += (_, _) => InputChanged();
            _input = display = combo;
        }
        else if (field.Display == "slider")
        {
            var maximum = (double)field.SliderMaximum(Math.Max(field.Minimum ?? 0,
                schema.EffectiveMaximum(field, values) ?? 100));
            var selected = decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                out var configured) ? Math.Clamp(configured, field.Minimum ?? 0, (decimal)maximum) :
                decimal.Parse(field.Default, CultureInfo.InvariantCulture);
            var slider = new Slider
            {
                Minimum = (double)(field.Minimum ?? 0), Maximum = maximum, Value = (double)selected,
                TickFrequency = (double)field.Step, IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center
            };
            _sliderValue = new TextBlock
            {
                Text = field.FormatSliderValue(slider.Value), MinWidth = 22,
                TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center
            };
            slider.ValueChanged += (_, _) =>
            {
                _sliderValue.Text = field.FormatSliderValue(slider.Value);
                InputChanged();
            };
            var sliderDisplay = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            sliderDisplay.Children.Add(slider);
            SetColumn(_sliderValue, 1);
            sliderDisplay.Children.Add(_sliderValue);
            _input = slider;
            display = sliderDisplay;
        }
        else if (field.Kind is FormFieldKind.Integer or FormFieldKind.Decimal)
        {
            var parsed = decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                out var number) ? number : decimal.Parse(field.Default, CultureInfo.InvariantCulture);
            var numeric = new NumericUpDown
            {
                Minimum = field.Minimum ?? -1000000,
                Maximum = Math.Max(field.Minimum ?? -1000000, schema.EffectiveMaximum(field, values) ?? 1000000),
                Value = parsed, Increment = field.Step,
                FormatString = field.Kind == FormFieldKind.Integer ? "0" : "0.##",
                ParsingNumberStyle = field.Kind == FormFieldKind.Integer ? NumberStyles.Integer : NumberStyles.Float,
                ClipValueToMinMax = true, ShowButtonSpinner = false
            };
            numeric.ValueChanged += (_, _) => InputChanged();
            _input = display = numeric;
        }
        else
        {
            var text = new TextBox
            {
                Text = value, MaxLength = field.MaxLength ?? 0,
                PasswordChar = field.Kind == FormFieldKind.Secret ? '●' : '\0'
            };
            text.TextChanged += (_, _) => InputChanged();
            _input = display = text;
        }
        SetColumn(display, 1);
        Children.Add(display);

        _reset = new IconButton
        {
            Width = 28, Height = 28, MinHeight = 28, IconSize = 15,
            IconData = Geometry.Parse("M 16,6 A 7,7 0 1 0 17,11 M 16,2 L 16,7 L 11,7"),
            IconStroke = new SolidColorBrush(Color.Parse("#C9B8E0")),
            VerticalAlignment = VerticalAlignment.Center
        };
        _reset.Classes.Add("icon-button");
        ToolTip.SetTip(_reset, AppTranslations.Get("form.reset_default", "Reset to default"));
        _reset.Click += (_, _) => ResetToDefault();
        SetColumn(_reset, 2);
        Children.Add(_reset);
        UpdateResetAvailability();
    }

    public void SetMaximum(decimal maximum)
    {
        maximum = Math.Max(_input is NumericUpDown number ? number.Minimum : _field.Minimum ?? 0, maximum);
        if (_input is Slider slider)
        {
            slider.Maximum = (double)_field.SliderMaximum(maximum);
            if (slider.Value > slider.Maximum) slider.Value = slider.Maximum;
        }
        else if (_input is NumericUpDown numeric)
        {
            numeric.Maximum = maximum;
            if (numeric.Value > numeric.Maximum) numeric.Value = numeric.Maximum;
        }
        UpdateResetAvailability();
    }

    public void ResetToDefault()
    {
        _settingValue = true;
        try
        {
            switch (_input)
            {
                case CheckBox box:
                    box.IsChecked = bool.Parse(_field.Default);
                    break;
                case ComboBox combo:
                    combo.SelectedItem = ((IEnumerable<ChoiceOption>)combo.ItemsSource!).FirstOrDefault(
                        option => option.Value == _field.Default);
                    break;
                case Slider slider:
                    slider.Value = Math.Clamp((double)decimal.Parse(_field.Default, CultureInfo.InvariantCulture),
                        slider.Minimum, slider.Maximum);
                    break;
                case NumericUpDown numeric:
                    numeric.Value = Math.Clamp(decimal.Parse(_field.Default, CultureInfo.InvariantCulture),
                        numeric.Minimum, numeric.Maximum);
                    break;
                case TextBox text:
                    text.Text = _field.Default;
                    break;
            }
        }
        finally { _settingValue = false; }
        InputChanged();
    }

    private void InputChanged()
    {
        if (_settingValue) return;
        UpdateResetAvailability();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateResetAvailability()
    {
        if (_input is Slider slider)
        {
            var bounded = Math.Clamp((double)decimal.Parse(_field.Default, CultureInfo.InvariantCulture),
                slider.Minimum, slider.Maximum);
            _reset.IsEnabled = Value != _field.FormatSliderValue(bounded);
        }
        else if (_input is NumericUpDown numeric)
        {
            var bounded = Math.Clamp(decimal.Parse(_field.Default, CultureInfo.InvariantCulture),
                numeric.Minimum, numeric.Maximum);
            _reset.IsEnabled = numeric.Value != bounded;
        }
        else
            _reset.IsEnabled = !string.Equals(Value, _field.Default,
                _field.Kind == FormFieldKind.Boolean ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
