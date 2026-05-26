using System;
using System.Diagnostics;
using System.Text;
using System.Windows.Automation;

namespace Activity_Tracker
{
    /// <summary>
    /// Utility class that uses Windows UI Automation to extract the active tab's URL 
    /// from Chromium-based browsers (Chrome, Edge, and Opera) in a high-performance, thread-safe manner.
    /// </summary>
    public static class BrowserUrlExtractor
    {
        /// <summary>
        /// Attempts to retrieve the active tab's URL from the specified window handle and application name.
        /// </summary>
        /// <param name="hwnd">The window handle (HWND) of the foreground window.</param>
        /// <param name="appName">The name of the application process (e.g. chrome.exe).</param>
        /// <returns>The normalized URL if successfully retrieved, or null otherwise.</returns>
        public static string? GetActiveTabUrl(IntPtr hwnd, string appName)
        {
            if (hwnd == IntPtr.Zero || string.IsNullOrEmpty(appName))
                return null;

            string lowerApp = appName.ToLower();
            bool isSupportedBrowser = lowerApp.Contains("chrome") || 
                                     lowerApp.Contains("msedge") || 
                                     lowerApp.Contains("opera");

            if (!isSupportedBrowser)
                return null;

            try
            {
                // Create an AutomationElement from the window handle
                AutomationElement windowElement = AutomationElement.FromHandle(hwnd);
                if (windowElement == null)
                    return null;

                // 1. Try finding by Automation ID first (extremely fast - Chrome, Edge, and Opera use "address_box")
                var idCondition = new PropertyCondition(AutomationElement.AutomationIdProperty, "address_box");
                AutomationElement addressBar = windowElement.FindFirst(TreeScope.Descendants, idCondition);

                if (addressBar != null)
                {
                    string? url = ExtractUrlFromElement(addressBar);
                    if (!string.IsNullOrEmpty(url))
                        return NormalizeUrl(url);
                }

                // 2. Fallback: Search for all Edit controls (more generic, handles older versions or customization)
                var editCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
                var edits = windowElement.FindAll(TreeScope.Descendants, editCondition);

                foreach (AutomationElement edit in edits)
                {
                    string? url = ExtractUrlFromElement(edit);
                    if (!string.IsNullOrEmpty(url) && IsValidUrlCandidate(url))
                    {
                        return NormalizeUrl(url);
                    }
                }
            }
            catch (Exception)
            {
                // UI Automation calls can throw exceptions if elements are dynamically destroyed 
                // while we are querying them (e.g. user closes browser or tab).
            }

            return null;
        }

        /// <summary>
        /// Extracts the text value from a UI Automation element using ValuePattern.
        /// </summary>
        private static string? ExtractUrlFromElement(AutomationElement element)
        {
            try
            {
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object patternObj))
                {
                    var valuePattern = (ValuePattern)patternObj;
                    return valuePattern.Current.Value;
                }
            }
            catch
            {
                // Element pattern query may fail if the control is no longer accessible
            }
            return null;
        }

        /// <summary>
        /// Validates whether a string has characteristics of a URL/domain.
        /// </summary>
        private static bool IsValidUrlCandidate(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;

            return value.Contains(".") || 
                   value.Contains("://") || 
                   value.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("about:", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Normalizes the URL string for consistent display (stripping tracking query params if needed, 
        /// and guaranteeing scheme protocol).
        /// </summary>
        private static string NormalizeUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) 
                return string.Empty;

            url = url.Trim();

            // Ignore blank or default address bar placeholders
            if (url.Equals("Search or type URL", StringComparison.OrdinalIgnoreCase) || 
                url.Equals("Address and search bar", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            // Ensure schema is attached if it's a domain name (e.g. "google.com" -> "https://google.com")
            if (!url.Contains("://") && !url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            {
                return "https://" + url;
            }

            return url;
        }
    }
}
