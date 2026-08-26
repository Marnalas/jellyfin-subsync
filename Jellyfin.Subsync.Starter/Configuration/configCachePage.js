'use strict';

const SEARCH_DEBOUNCE_MS = 300;
const SEARCH_LIMIT = 15;

const htmlEscapes = {'&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'};

function escapeHtml(text) {
    return (text || '').replace(/[&<>"']/g, function (c) {
        return htmlEscapes[c];
    });
}

function itemSubtitle(item) {
    if (item.SeriesName) return item.SeriesName;
    if (item.ProductionYear) return String(item.ProductionYear);
    return '';
}

function buildResultRowHtml(item) {
    const subtitle = itemSubtitle(item);
    return '' +
        '<div class="inputContainer itemResultRow" data-item-id="' + escapeHtml(item.Id) + '" style="display:flex;align-items:center;justify-content:space-between;gap:1em;">' +
        '<div>' +
        '<div class="itemResultName">' + escapeHtml(item.Name) + '</div>' +
        (subtitle ? '<div class="fieldDescription itemResultSubtitle">' + escapeHtml(subtitle) + '</div>' : '') +
        '</div>' +
        '<div class="itemResultAction">' +
        '<button is="emby-button" type="button" class="raised clearItemButton">' +
        '<span>Clear</span>' +
        '</button>' +
        '</div>' +
        '</div>';
}

// Shared between this page and the main Subsync page so both render the same
// tab strip via LibraryMenu.setTabs - only the active index differs per page.
function getTabs() {
    return [
        {href: Dashboard.getPluginUrl('Subsync'), name: 'Settings'},
        {href: Dashboard.getPluginUrl('Cache'), name: 'Cache'}
    ];
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
            limit: SEARCH_LIMIT
        }).then(function (result) {
            renderResults(result.Items || []);
        });
    }

    function clearItem(row) {
        const itemId = row.dataset.itemId;
        const action = row.querySelector('.itemResultAction');
        const button = row.querySelector('.clearItemButton');
        button.disabled = true;

        ApiClient.ajax({
            type: 'DELETE',
            url: ApiClient.getUrl('Subsync/SkipCache/Items/' + itemId),
            dataType: 'json'
        }).then(function (result) {
            action.textContent = result.removed > 0
                ? 'Cleared ' + result.removed
                : 'Nothing cached for this item';
        }).catch(function () {
            button.disabled = false;
            action.textContent = 'Failed to clear - try again';
        });
    }

    function clearAll() {
        if (!window.confirm('Clear the entire Subsync cache? Every synced subtitle will be checked again on the next sweep.'))
            return;

        const button = byId('ClearAllButton');
        const status = byId('ClearAllStatus');
        button.disabled = true;
        status.textContent = '';

        ApiClient.ajax({
            type: 'DELETE',
            url: ApiClient.getUrl('Subsync/SkipCache'),
            dataType: 'json'
        }).then(function (result) {
            button.disabled = false;
            status.textContent = result.removed > 0
                ? 'Cleared ' + result.removed + ' cached result(s).'
                : 'Cache was already empty.';
        }).catch(function () {
            button.disabled = false;
            status.textContent = 'Failed to clear the cache - try again.';
        });
    }

    view.addEventListener('viewshow', function () {
        LibraryMenu.setTabs('subsync', 1, getTabs);

        byId('ItemSearch').value = '';
        byId('ItemSearchResults').innerHTML = '';
        byId('ClearAllStatus').textContent = '';
    });

    byId('ClearAllButton').addEventListener('click', clearAll);

    byId('ItemSearch').addEventListener('input', function () {
        const term = this.value.trim();
        if (searchTimer) window.clearTimeout(searchTimer);
        searchTimer = window.setTimeout(function () {
            searchItems(term);
        }, SEARCH_DEBOUNCE_MS);
    });

    byId('ItemSearchResults').addEventListener('click', function (e) {
        const button = e.target.closest('.clearItemButton');
        if (!button) return;
        clearItem(button.closest('.itemResultRow'));
    });
}
