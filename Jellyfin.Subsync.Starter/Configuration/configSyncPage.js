'use strict';

const SEARCH_DEBOUNCE_MS = 300;
const SEARCH_LIMIT = 15;

const htmlEscapes = {'&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'};

function escapeHtml(text) {
    return (text || '').replace(/[&<>"']/g, function (c) {
        return htmlEscapes[c];
    });
}

function pad2(n) {
    return n < 10 ? '0' + n : String(n);
}

// 'S02E05', or just 'E05' when Jellyfin has no season number for this
// episode (e.g. some specials) - blank for anything that isn't an episode.
function episodeLabel(item) {
    if (typeof item.IndexNumber !== 'number') return '';
    const episode = 'E' + pad2(item.IndexNumber);
    return typeof item.ParentIndexNumber === 'number' ? 'S' + pad2(item.ParentIndexNumber) + episode : episode;
}

function itemSubtitle(item) {
    if (item.SeriesName) {
        const label = episodeLabel(item);
        return label ? item.SeriesName + ' • ' + label : item.SeriesName;
    }
    if (item.ProductionYear) return String(item.ProductionYear);
    return '';
}

function buildResultRowHtml(item) {
    const subtitle = itemSubtitle(item);
    return '' +
        '<div class="inputContainer itemResultRow" data-item-id="' + escapeHtml(item.Id) + '" style="display:flex;align-items:center;justify-content:space-between;gap:1em;">' +
        '<div style="min-width:0;">' +
        '<div class="itemResultName">' + escapeHtml(item.Name) + '</div>' +
        (subtitle ? '<div class="fieldDescription itemResultSubtitle">' + escapeHtml(subtitle) + '</div>' : '') +
        (item.Path ? '<div class="fieldDescription itemResultPath" style="word-break:break-all;">' + escapeHtml(item.Path) + '</div>' : '') +
        '<div class="fieldDescription itemResultSyncStatus"></div>' +
        '</div>' +
        '<div class="itemResultAction">' +
        '<button is="emby-button" type="button" class="raised syncItemButton">' +
        '<span>Sync</span>' +
        '</button>' +
        '</div>' +
        '</div>';
}

// Shared between this page and the Settings/Cache pages so all three render
// the same tab strip via LibraryMenu.setTabs - only the active index differs
// per page.
function getTabs() {
    return [
        {href: Dashboard.getPluginUrl('Subsync'), name: 'Settings'},
        {href: Dashboard.getPluginUrl('Cache'), name: 'Cache'},
        {href: Dashboard.getPluginUrl('Sync'), name: 'Sync'}
    ];
}

function renderSyncSummary(result) {
    if (!result.results || result.results.length === 0)
        return 'Nothing to sync (' + result.reason + ').';
    const synced = result.results.filter(function (r) {
        return r.outcome === 'Synced';
    }).length;
    return synced + ' of ' + result.results.length + ' subtitle(s) synced.';
}

export default function (view) {
    let searchTimer = null;

    function byId(id) {
        return view.querySelector('#' + id);
    }

    function renderResults(items) {
        const container = byId('ItemSearchResults');
        if (items.length === 0) {
            container.innerHTML = '<div class="fieldDescription">No matching items.</div>';
            return;
        }
        container.innerHTML = items.map(buildResultRowHtml).join('');
    }

    function searchItems(term) {
        if (!term) {
            byId('ItemSearchResults').innerHTML = '';
            return;
        }

        ApiClient.getItems(ApiClient.getCurrentUserId(), {
            searchTerm: term,
            includeItemTypes: 'Movie,Episode,Video,MusicVideo,Trailer',
            recursive: true,
            limit: SEARCH_LIMIT,
            fields: 'Path'
        }).then(function (result) {
            renderResults(result.Items || []);
        });
    }

    function syncItem(row) {
        const itemId = row.dataset.itemId;
        const status = row.querySelector('.itemResultSyncStatus');
        const button = row.querySelector('.syncItemButton');
        button.disabled = true;
        status.textContent = 'Syncing…';

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('Subsync/Sync/Items/' + itemId),
            dataType: 'json'
        }).then(function (result) {
            button.disabled = false;
            status.textContent = renderSyncSummary(result);
        }).catch(function (err) {
            button.disabled = false;
            status.textContent = err && err.status === 409
                ? 'A library sweep is currently running - try again once it finishes.'
                : 'Failed to sync - try again';
        });
    }

    view.addEventListener('viewshow', function () {
        LibraryMenu.setTabs('subsync', 2, getTabs);

        byId('ItemSearch').value = '';
        byId('ItemSearchResults').innerHTML = '';
    });

    byId('ItemSearch').addEventListener('input', function () {
        const term = this.value.trim();
        if (searchTimer) window.clearTimeout(searchTimer);
        searchTimer = window.setTimeout(function () {
            searchItems(term);
        }, SEARCH_DEBOUNCE_MS);
    });

    byId('ItemSearchResults').addEventListener('click', function (e) {
        const button = e.target.closest('.syncItemButton');
        if (!button) return;
        syncItem(button.closest('.itemResultRow'));
    });
}
