using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Shadowsocks.View.Controls
{
    public static class VisualTreeHelpers
    {
        public static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T target)
                {
                    return target;
                }

                current = current is Visual or Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
            }

            return null;
        }
    }
}
