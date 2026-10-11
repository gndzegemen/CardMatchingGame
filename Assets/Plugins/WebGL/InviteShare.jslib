// Room invites for the browser build: the native share sheet on phones, the clipboard elsewhere.
// CardMatching_ShareLink returns 1 when the share sheet opened, 2 when the link was copied, 0 on failure.
mergeInto(LibraryManager.library, {
  CardMatching_ShareLink: function (urlPtr, textPtr) {
    var url = UTF8ToString(urlPtr);
    var text = UTF8ToString(textPtr);

    // Fallback for pages without the async clipboard API (plain http) or when it is refused.
    function copyWithSelection() {
      var area = document.createElement("textarea");
      area.value = url;
      area.setAttribute("readonly", "");
      area.style.position = "fixed";
      area.style.top = "0";
      area.style.left = "0";
      area.style.opacity = "0";
      document.body.appendChild(area);
      area.focus();
      area.select();
      var copied = false;
      try { copied = document.execCommand("copy"); } catch (e) {}
      document.body.removeChild(area);
      var canvas = document.querySelector("#unity-canvas");
      if (canvas) canvas.focus();
      return copied;
    }

    var mobile = /iPhone|iPad|iPod|Android/i.test(navigator.userAgent);
    if (mobile && navigator.share) {
      try {
        navigator.share({ title: document.title, text: text, url: url }).catch(function () {});
        return 1;
      } catch (e) {}
    }
    if (navigator.clipboard && navigator.clipboard.writeText && window.isSecureContext) {
      try {
        navigator.clipboard.writeText(url).catch(function () { copyWithSelection(); });
        return 2;
      } catch (e) {}
    }
    return copyWithSelection() ? 2 : 0;
  }
});
