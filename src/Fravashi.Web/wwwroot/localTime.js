// Backlog 246 (replacing 234's text-node rewrite): the PERSON's time zone, which the browser knows and the server does
// not. PersonTimeZoneReader asks for it once per circuit, and the studio formats every time in it (PersonTime); this
// file no longer touches the page. An empty answer leaves the zone unknown, and the studio then says UTC, labelled.
(function () {
    'use strict';

    window.fravashiTimeZone = {
        read: function () {
            try {
                return Intl.DateTimeFormat().resolvedOptions().timeZone || '';
            } catch (e) {
                return '';
            }
        },
    };
})();
