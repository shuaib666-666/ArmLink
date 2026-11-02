using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace ArmLink.UI.Behaviors;

/// <summary>
/// 讓 ListBox 在新的項目加入時自動捲到最底端（日誌主控台必備）。
/// <para>寫成附加行為（Attached Behavior）而不是寫在 Code-behind，
/// 是為了維持 MVVM 純度：MainWindow.xaml.cs 裡不應該出現任何 UI 邏輯。</para>
/// </summary>
public static class AutoScrollBehavior
{
    /// <summary>是否啟用自動捲動。</summary>
    public static readonly DependencyProperty AutoScrollProperty =
        DependencyProperty.RegisterAttached(
            "AutoScroll",
            typeof(bool),
            typeof(AutoScrollBehavior),
            new PropertyMetadata(false, OnAutoScrollChanged));

    private static readonly DependencyPropertyKey HandlerKey =
        DependencyProperty.RegisterAttachedReadOnly(
            "Handler",
            typeof(NotifyCollectionChangedEventHandler),
            typeof(AutoScrollBehavior),
            new PropertyMetadata(null));

    /// <summary>取得附加屬性值。</summary>
    public static bool GetAutoScroll(DependencyObject element) => (bool)element.GetValue(AutoScrollProperty);

    /// <summary>設定附加屬性值。</summary>
    public static void SetAutoScroll(DependencyObject element, bool value) => element.SetValue(AutoScrollProperty, value);

    private static void OnAutoScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox listBox)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            if (listBox.IsLoaded)
            {
                Attach(listBox);
            }
            else
            {
                listBox.Loaded += (_, _) => Attach(listBox);
            }
        }
        else
        {
            Detach(listBox);
        }
    }

    private static void Attach(ListBox listBox)
    {
        Detach(listBox);

        // ItemsControl.Items 就是 INotifyCollectionChanged，直接訂閱最省事。
        if (listBox.Items is INotifyCollectionChanged notify)
        {
            NotifyCollectionChangedEventHandler handler = (_, args) =>
            {
                if (args.Action != NotifyCollectionChangedAction.Add)
                {
                    return;
                }

                if (listBox.Items.Count == 0)
                {
                    return;
                }

                listBox.ScrollIntoView(listBox.Items[^1]);
            };

            notify.CollectionChanged += handler;
            listBox.SetValue(HandlerKey, handler);
        }
    }

    private static void Detach(ListBox listBox)
    {
        if (listBox.GetValue(HandlerKey.DependencyProperty) is NotifyCollectionChangedEventHandler handler &&
            listBox.Items is INotifyCollectionChanged notify)
        {
            notify.CollectionChanged -= handler;
            listBox.SetValue(HandlerKey, null);
        }
    }
}
