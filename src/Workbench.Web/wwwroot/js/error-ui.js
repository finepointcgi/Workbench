// The Blazor error UI's "Report this" link: adds the page the error happened on (path only, never the query string)
// just before it's followed. A separate file because the Content-Security-Policy allows no inline script.
(function () {
    document.addEventListener('click', function (event) {
        var link = event.target && event.target.closest ? event.target.closest('#wb-error-report') : null;
        if (link) {
            link.setAttribute('href', 'support/new?from=error&page=' + encodeURIComponent(window.location.pathname));
        }
    }, true);
})();
