using CtrlTNav.Core.Model;

namespace CtrlTNav.Core.Services
{
    /// <summary>Opens the file of a navigation item and moves the caret to its location.</summary>
    public interface INavigationService
    {
        /// <summary>
        /// Navigates to <paramref name="item"/>. Returns false and sets <paramref name="errorMessage"/>
        /// when the file cannot be opened.
        /// </summary>
        bool Navigate(NavigationItem item, out string? errorMessage);
    }
}
