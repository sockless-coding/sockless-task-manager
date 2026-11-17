using System.Diagnostics;
using Sockless.TaskManager.Core.Interop;

namespace Sockless.TaskManager.Features.Drivers;

/// <summary>
/// Driver updates through the Windows Update Agent's COM interfaces, called by vtable (slots from
/// the SDK's wuapi.h). Searching works for every user and asks Windows Update (or the organisation's
/// update server) online, which can take a minute; downloading and installing need an administrator.
/// Each call runs on its own thread in the multithreaded apartment, so an update is found again by
/// its id to install it rather than kept between calls.
/// </summary>
internal static unsafe class WindowsUpdate
{
    private const string ClientId = "Sockless Task Manager";
    private const short VariantTrue = -1;

    // OperationResultCode.
    private const int Succeeded = 2;
    private const int SucceededWithErrors = 3;
    private const int Aborted = 5;

    private static readonly Guid ClsidUpdateSession = new("4CB43D7F-7EEE-4906-8698-60DA1C38F2FE");
    private static readonly Guid IidUpdateSession = new("816858a4-260d-4260-933a-2585f1abc76b");
    private static readonly Guid ClsidUpdateCollection = new("13639463-00DB-4646-803D-528026140D88");
    private static readonly Guid IidUpdateCollection = new("07f7438c-7709-4ca5-b518-91279288134e");
    private static readonly Guid IidWindowsDriverUpdate = new("b383cd1a-5ce9-4504-9f63-764b1236f191");
    private static readonly Guid IidUpdate3 = new("112EDA6B-95B3-476F-9D90-AEE82C6B8181");

    /// <summary>Driver updates offered for this computer, or why they couldn't be read.</summary>
    public static (List<DriverUpdate> Updates, string? Failure) Search()
    {
        var updates = new List<DriverUpdate>();
        var hr = WithSearch("IsInstalled=0 and Type='Driver' and IsHidden=0", collection =>
        {
            foreach (var update in Items(collection))
            {
                try
                {
                    if (Read(update) is { } read)
                        updates.Add(read);
                }
                finally
                {
                    Com.Release(update);
                }
            }

            return 0;
        });

        return (updates, hr < 0 ? Describe(hr) : null);
    }

    /// <summary>
    /// Driver updates Windows Update installed, from its own history (local; nothing goes online):
    /// when, and the title, which ends with the driver's version ("NVIDIA Display Driver Update (32.0.16.1088)").
    /// </summary>
    public static List<(DateTime Time, string Title)> History()
    {
        var installed = new List<(DateTime, string)>();
        WithSession(session =>
        {
            nint searcher;
            var hr = ((delegate* unmanaged<nint, nint*, int>)(*(nint**)session)[12])(session, &searcher);
            if (hr < 0)
                return hr;

            try
            {
                // IUpdateSearcher: QueryHistory 18, GetTotalHistoryCount 22.
                int total;
                if (Call(searcher, 22, &total) < 0 || total <= 0)
                    return 0;

                nint entries;
                hr = ((delegate* unmanaged<nint, int, int, nint*, int>)(*(nint**)searcher)[18])(searcher, 0, Math.Min(total, 2000), &entries);
                if (hr < 0)
                    return hr;

                try
                {
                    // IUpdateHistoryEntryCollection: get_Item 7, get_Count 9.
                    int count;
                    if (Call(entries, 9, &count) < 0)
                        return 0;

                    var getItem = (delegate* unmanaged<nint, int, nint*, int>)(*(nint**)entries)[7];
                    for (var i = 0; i < count; i++)
                    {
                        nint entry;
                        if (getItem(entries, i, &entry) < 0 || entry == 0)
                            continue;

                        try
                        {
                            // IUpdateHistoryEntry: get_Operation 7 (1 = install), get_ResultCode 8, get_Date 10, get_Title 12.
                            int operation = 0, code = 0;
                            double date = 0;
                            Call(entry, 7, &operation);
                            Call(entry, 8, &code);
                            Call(entry, 10, &date);
                            if (operation == 1 && code is Succeeded or SucceededWithErrors && Text(entry, 12) is { } title && date > 0)
                                installed.Add((DateTime.SpecifyKind(DateTime.FromOADate(date), DateTimeKind.Utc), title));
                        }
                        finally
                        {
                            Com.Release(entry);
                        }
                    }

                    return 0;
                }
                finally
                {
                    Com.Release(entries);
                }
            }
            finally
            {
                Com.Release(searcher);
            }
        });

        return installed;
    }

    /// <summary>
    /// Downloads and installs one update, found again by its id. <paramref name="progress"/> hears each
    /// step. Returns why it failed (null on success) and whether a restart finishes it.
    /// </summary>
    public static (string? Failure, bool Restart) Install(string updateId, Action<string> progress)
    {
        var restart = false;
        string? failure = null;
        var hr = WithSession(session =>
        {
            progress("Finding the update…");
            nint found = 0;
            var search = Search(session, $"UpdateID='{updateId.Replace("'", string.Empty)}'", collection =>
            {
                var items = Items(collection);
                if (items.Count > 0)
                    found = items[0];
                foreach (var extra in items.Skip(1))
                    Com.Release(extra);
                return 0;
            });

            if (search < 0)
                return search;
            if (found == 0)
            {
                failure = "Windows Update no longer offers it.";
                return 0;
            }

            try
            {
                // IUpdate: EulaAccepted 16, AcceptEula 47. Driver updates rarely have one; accept it if so.
                short accepted;
                if (Call(found, 16, &accepted) >= 0 && accepted == 0)
                    ((delegate* unmanaged<nint, int>)(*(nint**)found)[47])(found);

                var collection = NewCollection(found);
                if (collection == 0)
                    return unchecked((int)0x80004005);

                try
                {
                    progress("Downloading…");
                    var downloaded = Run(session, 13, collection, downloader: true);
                    if (downloaded.Hr < 0 || downloaded.Result is not (Succeeded or SucceededWithErrors))
                    {
                        failure = downloaded.Result == Aborted ? "The download was stopped." : Describe(downloaded.Hr < 0 ? downloaded.Hr : downloaded.ItemHr) ?? "The download failed.";
                        return 0;
                    }

                    progress("Installing…");
                    var installed = Run(session, 14, collection, downloader: false);
                    if (installed.Hr < 0 || installed.Result is not (Succeeded or SucceededWithErrors))
                    {
                        failure = installed.Result == Aborted ? "The installation was stopped." : Describe(installed.Hr < 0 ? installed.Hr : installed.ItemHr) ?? "The installation failed.";
                        return 0;
                    }

                    restart = installed.Restart;
                    return 0;
                }
                finally
                {
                    Com.Release(collection);
                }
            }
            finally
            {
                Com.Release(found);
            }
        });

        return (failure ?? (hr < 0 ? Describe(hr) : null), restart);
    }

    private static int WithSearch(string criteria, Func<nint, int> action) => WithSession(session => Search(session, criteria, action));

    private static int WithSession(Func<nint, int> action)
    {
        try
        {
            return Com.OnThread(() =>
            {
                var clsid = ClsidUpdateSession;
                var iid = IidUpdateSession;
                nint session;
                var hr = Com.CoCreateInstance(&clsid, 0, Com.ClsctxInprocServer, &iid, &session);
                if (hr < 0)
                    return hr;

                try
                {
                    // IUpdateSession: put_ClientApplicationID 8.
                    SetText(session, 8, ClientId);
                    return action(session);
                }
                finally
                {
                    Com.Release(session);
                }
            });
        }
        catch (InvalidOperationException ex)
        {
            Debug.WriteLine($"Windows Update failed: {ex}");
            return unchecked((int)0x80004005);
        }
    }

    private static int Search(nint session, string criteria, Func<nint, int> action)
    {
        // IUpdateSession: CreateUpdateSearcher 12.
        nint searcher;
        var hr = ((delegate* unmanaged<nint, nint*, int>)(*(nint**)session)[12])(session, &searcher);
        if (hr < 0)
            return hr;

        try
        {
            // IUpdateSearcher: put_ClientApplicationID 10, Search 19.
            SetText(searcher, 10, ClientId);
            var bstr = Com.Bstr(criteria);
            nint result;
            try
            {
                hr = ((delegate* unmanaged<nint, nint, nint*, int>)(*(nint**)searcher)[19])(searcher, bstr, &result);
            }
            finally
            {
                Com.SysFreeString(bstr);
            }

            if (hr < 0)
                return hr;

            try
            {
                // ISearchResult: get_ResultCode 7, get_Updates 9.
                int code;
                if (Call(result, 7, &code) >= 0 && code is not (Succeeded or SucceededWithErrors))
                    return unchecked((int)0x80240FFF);

                nint collection;
                hr = Call(result, 9, &collection);
                if (hr < 0)
                    return hr;

                try
                {
                    return action(collection);
                }
                finally
                {
                    Com.Release(collection);
                }
            }
            finally
            {
                Com.Release(result);
            }
        }
        finally
        {
            Com.Release(searcher);
        }
    }

    /// <summary>A downloader (slot 13) or installer (slot 14) for <paramref name="collection"/>, run to the end.</summary>
    private static (int Hr, int Result, int ItemHr, bool Restart) Run(nint session, int create, nint collection, bool downloader)
    {
        nint worker;
        var hr = ((delegate* unmanaged<nint, nint*, int>)(*(nint**)session)[create])(session, &worker);
        if (hr < 0)
            return (hr, 0, 0, false);

        try
        {
            // IUpdateDownloader: put_ClientApplicationID 8, put_Updates 14, Download 16.
            // IUpdateInstaller: put_ClientApplicationID 8, put_Updates 16, Install 21.
            SetText(worker, 8, ClientId);
            hr = ((delegate* unmanaged<nint, nint, int>)(*(nint**)worker)[downloader ? 14 : 16])(worker, collection);
            if (hr < 0)
                return (hr, 0, 0, false);

            nint result;
            hr = ((delegate* unmanaged<nint, nint*, int>)(*(nint**)worker)[downloader ? 16 : 21])(worker, &result);
            if (hr < 0)
                return (hr, 0, 0, false);

            try
            {
                // IDownloadResult: get_HResult 7, get_ResultCode 8. IInstallationResult: get_HResult 7, get_RebootRequired 8, get_ResultCode 9.
                int itemHr = 0, code = 0;
                short restart = 0;
                Call(result, 7, &itemHr);
                Call(result, downloader ? 8 : 9, &code);
                if (!downloader)
                    Call(result, 8, &restart);
                return (0, code, itemHr, restart != 0);
            }
            finally
            {
                Com.Release(result);
            }
        }
        finally
        {
            Com.Release(worker);
        }
    }

    private static nint NewCollection(nint update)
    {
        var clsid = ClsidUpdateCollection;
        var iid = IidUpdateCollection;
        nint collection;
        if (Com.CoCreateInstance(&clsid, 0, Com.ClsctxInprocServer, &iid, &collection) < 0)
            return 0;

        // IUpdateCollection: Add 12.
        int index;
        if (((delegate* unmanaged<nint, nint, int*, int>)(*(nint**)collection)[12])(collection, update, &index) < 0)
        {
            Com.Release(collection);
            return 0;
        }

        return collection;
    }

    private static DriverUpdate? Read(nint update)
    {
        // IUpdate: get_Title 7, get_AutoSelectOnWebSites 8, get_Description 15, get_Identity 19, get_IsDownloaded 23.
        var title = Text(update, 7);
        var description = Text(update, 15);
        short downloaded = 0, autoSelect = 0;
        Call(update, 23, &downloaded);
        Call(update, 8, &autoSelect);

        string? id = null;
        nint identity;
        if (Call(update, 19, &identity) >= 0 && identity != 0)
        {
            // IUpdateIdentity: get_UpdateID 8.
            id = Text(identity, 8);
            Com.Release(identity);
        }

        if (id is null || title is null)
            return null;

        // IUpdate3: get_BrowseOnly 56 (optional in Settings). Fall back to "not selected automatically".
        var optional = autoSelect == 0;
        if (Query(update, IidUpdate3) is { } update3)
        {
            short browseOnly;
            if (Call(update3, 56, &browseOnly) >= 0)
                optional = browseOnly != 0;
            Com.Release(update3);
        }

        string? hardwareId = null, provider = null, manufacturer = null, model = null, driverClass = null;
        DateTime? date = null;
        if (Query(update, IidWindowsDriverUpdate) is { } driver)
        {
            // IWindowsDriverUpdate: DriverClass 52, DriverHardwareID 53, DriverManufacturer 54, DriverModel 55, DriverProvider 56, DriverVerDate 57.
            driverClass = Text(driver, 52);
            hardwareId = Text(driver, 53);
            manufacturer = Text(driver, 54);
            model = Text(driver, 55);
            provider = Text(driver, 56);
            double oaDate;
            if (Call(driver, 57, &oaDate) >= 0 && oaDate > 0)
                date = DateTime.FromOADate(oaDate);
            Com.Release(driver);
        }

        return new DriverUpdate(id, title, description, hardwareId, provider, manufacturer, model, driverClass, date, optional, downloaded != 0);
    }

    /// <summary>The items of an IUpdateCollection (get_Item 7, get_Count 10). The caller releases each.</summary>
    private static List<nint> Items(nint collection)
    {
        var items = new List<nint>();
        int count;
        if (Call(collection, 10, &count) < 0)
            return items;

        var getItem = (delegate* unmanaged<nint, int, nint*, int>)(*(nint**)collection)[7];
        for (var i = 0; i < count; i++)
        {
            nint item;
            if (getItem(collection, i, &item) >= 0 && item != 0)
                items.Add(item);
        }

        return items;
    }

    private static nint? Query(nint unknown, Guid iid)
    {
        nint result;
        return ((delegate* unmanaged<nint, Guid*, nint*, int>)(*(nint**)unknown)[0])(unknown, &iid, &result) >= 0 && result != 0 ? result : null;
    }

    private static int Call<T>(nint target, int slot, T* value) where T : unmanaged =>
        ((delegate* unmanaged<nint, T*, int>)(*(nint**)target)[slot])(target, value);

    private static string? Text(nint target, int slot)
    {
        nint bstr;
        if (Call(target, slot, &bstr) < 0 || bstr == 0)
            return null;

        var text = Com.TakeBstr(bstr).Trim();
        return text.Length == 0 ? null : text;
    }

    private static void SetText(nint target, int slot, string text)
    {
        var bstr = Com.Bstr(text);
        try
        {
            ((delegate* unmanaged<nint, nint, int>)(*(nint**)target)[slot])(target, bstr);
        }
        finally
        {
            Com.SysFreeString(bstr);
        }
    }

    /// <summary>Windows Update's errors in words; null for success.</summary>
    private static string? Describe(int hr) => hr switch
    {
        >= 0 => null,
        unchecked((int)0x80070005) or unchecked((int)0x80240044) => "Windows refused: installing drivers needs administrator rights.",
        unchecked((int)0x8024402C) or unchecked((int)0x80072EE7) or unchecked((int)0x80072EFD) or unchecked((int)0x80072EE2) or unchecked((int)0x8024401C)
            => "Windows Update couldn't be reached. Check the internet connection.",
        unchecked((int)0x8024001E) => "Windows Update is shutting down; try again in a moment.",
        unchecked((int)0x80240016) => "Windows Update is busy installing something else; try again when it's done.",
        unchecked((int)0x8024A000) => "Windows Update is busy; try again in a moment.",
        unchecked((int)0x80070422) => "The Windows Update service is turned off.",
        unchecked((int)0x8024002E) => "Windows Update is turned off by policy on this computer.",
        unchecked((int)0x80240FFF) => "Windows Update couldn't finish the search.",
        _ => $"Windows Update couldn't do that (0x{hr:X8}).",
    };
}
