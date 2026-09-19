'use strict';

const SEARCH_DEBOUNCE_MS = 300;
const SEARCH_LIMIT = 15;
const SERIES_MATCH_LIMIT = 5;

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
        '<div class="inputContainer itemResultRow" data-item-id="' + escapeHtml(item.Id) + '" ' +
        'style="border-bottom:1px solid rgba(128,128,128,.25);padding-bottom:0.75em;margin-bottom:0.75em;">' +
        '<div style="display:flex;align-items:center;justify-content:space-between;gap:1em;">' +
        '<div style="min-width:0;">' +
        '<div class="itemResultName">' + escapeHtml(item.Name) + '</div>' +
        (subtitle ? '<div class="fieldDescription itemResultSubtitle">' + escapeHtml(subtitle) + '</div>' : '') +
        (item.Path ? '<div class="fieldDescription itemResultPath" style="word-break:break-all;">' + escapeHtml(item.Path) + '</div>' : '') +
        '<div class="fieldDescription itemResultClearStatus"></div>' +
        '</div>' +
        '<div class="itemResultAction" style="display:flex;flex-direction:column;align-items:stretch;gap:0.25em;">' +
        '<button is="emby-button" type="button" class="raised clearItemButton">' +
        '<span>Clear</span>' +
        '</button>' +
        '<button is="emby-button" type="button" class="raised clearItemFailuresButton">' +
        '<span>Clear failures</span>' +
        '</button>' +
        '</div>' +
        '</div>' +
        '</div>';
}

// Shared between this page and the Settings/Sync pages so all three render
// the same tab strip via LibraryMenu.setTabs - only the active index differs
// per page.
function getTabs() {
    return [
        {href: Dashboard.getPluginUrl('Subsync'), name: 'Settings'},
        {href: Dashboard.getPluginUrl('Cache'), name: 'Cache'},
        {href: Dashboard.getPluginUrl('Sync'), name: 'Sync'}
    ];
}

export default function (view) {
    let searchTimer = null;

    function byId(id) {
        return view.querySelector('#' + id);
    }

    function renderResults(items) {
        const container = byId('ItemSearchResults');
        // Drawn on the container, not each row, so it appears once above the
        // first row - separating the results from the search box/
        // instructions above - rather than as a permanent line under an
        // empty box.
        container.style.borderTop = '1px solid rgba(128,128,128,.25)';
        container.style.paddingTop = '0.75em';
        container.style.marginTop = '0.5em';
        if (items.length === 0) {
            container.innerHTML = '<div class="fieldDescription">No matching items.</div>';
            return;
        }
        container.innerHTML = items.map(buildResultRowHtml).join('');
    }

    function searchItems(term) {
        const container = byId('ItemSearchResults');
        if (!term) {
            container.innerHTML = '';
            container.style.borderTop = '';
            return;
        }

        const userId = ApiClient.getCurrentUserId();

        // Items' own searchTerm only matches an item's own name/title
        // (verified against Jellyfin's SqlSearchProvider source - it only
        // queries CleanName/OriginalTitle, never a parent's), so an episode
        // never matches a search for its show's name. There's no single
        // query for that: separately find series whose name matches, then
        // pull in every episode under each (a recursive query scoped to
        // that series' own id), merged with the direct name-based hits.
        const directMatch = ApiClient.getItems(userId, {
            searchTerm: term,
            includeItemTypes: 'Movie,Episode,Video,MusicVideo,Trailer',
            recursive: true,
            limit: SEARCH_LIMIT,
            fields: 'Path'
        }).then(function (result) {
            return result.Items || [];
        });

        const seriesMatch = ApiClient.getItems(userId, {
            searchTerm: term,
            includeItemTypes: 'Series',
            recursive: true,
            limit: SERIES_MATCH_LIMIT
        }).then(function (result) {
            const series = result.Items || [];
            return Promise.all(series.map(function (s) {
                return ApiClient.getItems(userId, {
                    parentId: s.Id,
                    includeItemTypes: 'Episode',
                    recursive: true,
                    limit: SEARCH_LIMIT,
                    fields: 'Path'
                }).then(function (episodes) {
                    return episodes.Items || [];
                });
            }));
        }).then(function (episodesPerSeries) {
            return [].concat.apply([], episodesPerSeries);
        });

        Promise.all([directMatch, seriesMatch]).then(function (results) {
            const seen = {};
            const merged = [];
            results[0].concat(results[1]).forEach(function (item) {
                if (seen[item.Id]) return;
                seen[item.Id] = true;
                merged.push(item);
            });
            renderResults(merged.slice(0, SEARCH_LIMIT));
        });
    }

    const CLEAR_ITEM_ALL = {
        pathSuffix: '',
        buttonSelector: '.clearItemButton',
        describe: function (result) {
            const total = result.removed + result.removedFailures;
            return total > 0 ? 'Cleared ' + total : 'Nothing cached for this item';
        }
    };

    const CLEAR_ITEM_FAILURES = {
        pathSuffix: '/Failures',
        buttonSelector: '.clearItemFailuresButton',
        describe: function (result) {
            return result.removedFailures > 0
                ? 'Cleared ' + result.removedFailures + ' failure(s)'
                : 'No failures for this item';
        }
    };

    function clearItem(row, opts) {
        const itemId = row.dataset.itemId;
        const status = row.querySelector('.itemResultClearStatus');
        const button = row.querySelector(opts.buttonSelector);
        button.disabled = true;
        status.textContent = 'Clearing…';

        ApiClient.ajax({
            type: 'DELETE',
            url: ApiClient.getUrl('Subsync/SkipCache/' + itemId + opts.pathSuffix),
            dataType: 'json'
        }).then(function (result) {
            button.disabled = false;
            status.textContent = opts.describe(result);
        }).catch(function () {
            button.disabled = false;
            status.textContent = 'Failed to clear - try again';
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
            const total = result.removed + result.removedFailures;
            status.textContent = total > 0
                ? 'Cleared ' + total + ' cached result(s).'
                : 'Cache was already empty.';
        }).catch(function () {
            button.disabled = false;
            status.textContent = 'Failed to clear the cache - try again.';
        });
    }

    function clearAllFailures() {
        if (!window.confirm('Clear all failure records? Subtitles that kept failing will be retried on the next sweep.'))
            return;

        const button = byId('ClearAllFailuresButton');
        const status = byId('ClearAllFailuresStatus');
        button.disabled = true;
        status.textContent = '';

        ApiClient.ajax({
            type: 'DELETE',
            url: ApiClient.getUrl('Subsync/SkipCache/Failures'),
            dataType: 'json'
        }).then(function (result) {
            button.disabled = false;
            status.textContent = result.removedFailures > 0
                ? 'Cleared ' + result.removedFailures + ' failure record(s).'
                : 'No failures were cached.';
        }).catch(function () {
            button.disabled = false;
            status.textContent = 'Failed to clear failures - try again.';
        });
    }

    view.addEventListener('viewshow', function () {
        LibraryMenu.setTabs('subsync', 1, getTabs);

        byId('ItemSearch').value = '';
        const results = byId('ItemSearchResults');
        results.innerHTML = '';
        results.style.borderTop = '';
        byId('ClearAllStatus').textContent = '';
        byId('ClearAllFailuresStatus').textContent = '';
    });

    byId('ClearAllButton').addEventListener('click', clearAll);
    byId('ClearAllFailuresButton').addEventListener('click', clearAllFailures);

    byId('ItemSearch').addEventListener('input', function () {
        const term = this.value.trim();
        if (searchTimer) window.clearTimeout(searchTimer);
        searchTimer = window.setTimeout(function () {
            searchItems(term);
        }, SEARCH_DEBOUNCE_MS);
    });

    byId('ItemSearchResults').addEventListener('click', function (e) {
        const clearButton = e.target.closest('.clearItemButton');
        if (clearButton) {
            clearItem(clearButton.closest('.itemResultRow'), CLEAR_ITEM_ALL);
            return;
        }

        const clearFailuresButton = e.target.closest('.clearItemFailuresButton');
        if (clearFailuresButton) {
            clearItem(clearFailuresButton.closest('.itemResultRow'), CLEAR_ITEM_FAILURES);
        }
    });
}
