using System.Reflection;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sbzilla.ServiceBus;

namespace Sbzilla.Views;

/// <summary>
/// Editable form for a queue/topic/subscription's settings, built from <see cref="EntityPropertyFields"/> over either a
/// Create…Options object (new entity) or a …Properties object (existing entity).
/// </summary>
public sealed class PropertyForm
{
    private static readonly EntityStatus[] Statuses = [EntityStatus.Active, EntityStatus.Disabled, EntityStatus.SendDisabled, EntityStatus.ReceiveDisabled];

    private readonly object _target;
    private readonly List<(PropertyField Field, PropertyInfo Property, Control Editor)> _rows = new();

    public PropertyForm(EntityKind kind, object target, bool readOnly)
    {
        _target = target;
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var index = 0;
        foreach (var (field, property) in EntityPropertyFields.Bind(kind, target))
        {
            var value = property.GetValue(target);
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var editable = !readOnly && property.CanWrite;
            Control editor;
            if (type == typeof(bool))
                editor = new ToggleSwitch { Header = field.Label, IsOn = value is true, OnContent = "Yes", OffContent = "No" };
            else if (type == typeof(EntityStatus))
            {
                var combo = new ComboBox { Header = field.Label, HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var s in Statuses) combo.Items.Add(s.ToString());
                combo.SelectedIndex = Math.Max(0, Array.IndexOf(Statuses, value is EntityStatus st ? st : EntityStatus.Active));
                editor = combo;
            }
            else
                editor = new TextBox { Header = field.Label, Text = EntityPropertyFields.Format(value), PlaceholderText = field.Hint, IsSpellCheckEnabled = false };
            editor.IsEnabled = editable;
            ToolTipService.SetToolTip(editor, property.CanWrite
                ? (field.Hint.Length > 0 ? field.Hint : field.Label)
                : $"{field.Label} can only be set when the entity is created");

            if (index % 2 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(editor, index / 2);
            Grid.SetColumn(editor, index % 2);
            grid.Children.Add(editor);
            _rows.Add((field, property, editor));
            index++;
        }
        if (index == 0) grid.Children.Add(new TextBlock { Text = "No editable settings.", Foreground = secondary });
        View = grid;
    }

    public FrameworkElement View { get; }

    /// <summary>Copies the form into the target object. Throws FormatException naming the bad field.</summary>
    public void Apply()
    {
        foreach (var (field, property, editor) in _rows)
        {
            if (!property.CanWrite) continue;
            object? value;
            try
            {
                value = editor switch
                {
                    ToggleSwitch t => t.IsOn,
                    ComboBox c => Statuses[Math.Max(0, c.SelectedIndex)],
                    TextBox t => EntityPropertyFields.Parse(property.PropertyType, t.Text),
                    _ => property.GetValue(_target),
                };
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                throw new FormatException($"{field.Label}: {ex.Message}", ex);
            }
            // Required value types (sizes, durations) can't be cleared; strings can.
            if (value is null && property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) == null)
                throw new FormatException($"{field.Label}: enter a value.");
            property.SetValue(_target, value);
        }
    }
}
