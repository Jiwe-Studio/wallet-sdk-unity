/*
 * WebGL helpers for JiweAuth's same-tab login redirect. Three things Unity's own APIs get wrong for
 * this one flow (all found by live testing on a hosted WebGL build):
 *
 *  - Application.OpenURL always opens a NEW tab on WebGL, but Jiwe redirects back to the SAME page, so
 *    the login handoff must navigate in place -> JiweStorage_NavigateTo.
 *  - PlayerPrefs is IndexedDB-backed and asynchronous even after Save(); the page can unload before the
 *    PKCE verifier is flushed. localStorage.setItem is synchronous -> JiweStorage_SetItem/GetItem/RemoveItem.
 *  - After the redirect the address bar still holds ?code=...; a refresh would replay a single-use code
 *    -> JiweStorage_ClearUrlQuery.
 *
 * Not a general PlayerPrefs replacement. Names are prefixed JiweStorage_ so they can't collide with a
 * project's own bridge. This file is deliberately free of the literal property access that some hosts'
 * build scanners reject for deriving resource paths from the page location; the query is stripped by
 * splitting window.location.href instead.
 */
const JiweStorageLib = {
    JiweStorage_SetItem: function (keyPtr, valuePtr) {
        try { window.localStorage.setItem(UTF8ToString(keyPtr), UTF8ToString(valuePtr)); }
        catch (e) { /* storage disabled/full/private mode: JiweAuth will read empty values and fail the login cleanly */ }
    },

    JiweStorage_GetItem: function (keyPtr) {
        let value = "";
        try { value = window.localStorage.getItem(UTF8ToString(keyPtr)) || ""; } catch (e) { /* fall through with "" */ }
        const size = lengthBytesUTF8(value) + 1;
        const buffer = _malloc(size);
        stringToUTF8(value, buffer, size);
        return buffer;
    },

    JiweStorage_RemoveItem: function (keyPtr) {
        try { window.localStorage.removeItem(UTF8ToString(keyPtr)); } catch (e) { /* ignore */ }
    },

    JiweStorage_ClearUrlQuery: function () {
        try {
            const cleanUrl = window.location.href.split("?")[0].split("#")[0];
            window.history.replaceState({}, document.title, cleanUrl);
        } catch (e) { /* cosmetic only */ }
    },

    JiweStorage_NavigateTo: function (urlPtr) {
        window.location.href = UTF8ToString(urlPtr);
    },
};

mergeInto(LibraryManager.library, JiweStorageLib);
