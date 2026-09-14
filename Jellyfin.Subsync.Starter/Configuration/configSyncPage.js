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
        '<div class="inputContainer itemResultRow" data-item-id="' + escapeHtml(item.Id) + '" ' +
        'style="border-bottom:1px solid rgba(128,128,128,.25);padding-bottom:0.75em;margin-bottom:0.75em;">' +
        '<div style="display:flex;align-items:center;justify-content:space-between;gap:1em;">' +
        '<div style="min-width:0;">' +
        '<div class="itemResultName">' + escapeHtml(item.Name) + '</div>' +
        (subtitle ? '<div class="fieldDescription itemResultSubtitle">' + escapeHtml(subtitle) + '</div>' : '') +
        (item.Path ? '<div class="fieldDescription itemResultPath" style="word-break:break-all;">' + escapeHtml(item.Path) + '</div>' : '') +
        '<div class="fieldDescription itemResultSyncStatus"></div>' +
        '</div>' +
        '<div class="itemResultAction" style="display:flex;flex-direction:column;align-items:stretch;gap:0.25em;">' +
        '<button is="emby-button" type="button" class="raised syncItemButton">' +
        '<span>Sync</span>' +
        '</button>' +
        '<button is="emby-button" type="button" class="raised syncOneToggle">' +
        '<span>Sync one subtitle…</span>' +
        '</button>' +
        '</div>' +
        '</div>' +
        '<div class="itemResultSubtitlePanel" style="margin-top:0.5em;" hidden></div>' +
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

// Labels a subtitle candidate for a picker: prefers "Title (language)",
// falls back to whichever of the two is present, and falls back further to
// its stream index when Jellyfin has neither - then flags forced/
// already-synced tracks, the latter being the "best case" reference the
// issue this feature closes is about (a sibling the admin already knows is
// correctly synced).
function subtitleOptionLabel(candidate) {
    const base = candidate.title && candidate.language
        ? candidate.title + ' (' + candidate.language + ')'
        : (candidate.title || candidate.language || ('Track ' + candidate.index));

    const flags = [];
    if (candidate.isForced) flags.push('forced');
    if (candidate.isAlreadySynced) flags.push('already synced');
    return flags.length ? base + ' — ' + flags.join(', ') : base;
}

function buildReferenceOptionsHtml(subtitles, excludeIndex) {
    return '<option value="">Video (default)</option>' +
        subtitles
            .filter(function (c) {
                return c.index !== excludeIndex;
            })
            .map(function (c) {
                return '<option value="' + c.index + '">' + escapeHtml(subtitleOptionLabel(c)) + '</option>';
            })
            .join('');
}

function buildSubtitlePanelHtml(data) {
    const subtitles = data.subtitles || [];
    if (subtitles.length === 0)
        return '<div class="fieldDescription">No eligible subtitles to sync individually (' + escapeHtml(data.reason) + ').</div>';

    const targetOptions = subtitles.map(function (c) {
        return '<option value="' + c.index + '">' + escapeHtml(subtitleOptionLabel(c)) + '</option>';
    }).join('');

    return '' +
        '<div class="inputContainer">' +
        '<select is="emby-select" class="subtitleTargetSelect" label="Subtitle to sync">' + targetOptions + '</select>' +
        '</div>' +
        '<div class="inputContainer">' +
        '<select is="emby-select" class="subtitleReferenceSelect" label="Sync against"></select>' +
        '</div>' +
        '<button is="emby-button" type="button" class="raised syncOneSubtitleButton">' +
        '<span>Sync selected</span>' +
        '</button>' +
        '<div class="fieldDescription syncOneStatus"></div>';
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

        // Items' own searchTerm only matches an item's own name, so an
        // episode never matches a search for its show. Search/Hints is
        // built for exactly this - each hint carries the matched item's
        // series, if any - but it doesn't carry Path, so the hits are
        // batch-resolved to full items (one extra call, not one per item)
        // and re-ordered back to the hints' own relevance order.
        ApiClient.ajax({
            type: 'GET',
            url: ApiClient.getUrl('Search/Hints', {
                searchTerm: term,
                includeItemTypes: 'Movie,Episode,Video,MusicVideo,Trailer',
                limit: SEARCH_LIMIT
            }),
            dataType: 'json'
        }).then(function (result) {
            const hints = result.SearchHints || [];
            if (hints.length === 0) {
                renderResults([]);
                return;
            }

            ApiClient.getItems(ApiClient.getCurrentUserId(), {
                ids: hints.map(function (h) {
                    return h.ItemId || h.Id;
                }).join(','),
                fields: 'Path'
            }).then(function (full) {
                const itemsById = {};
                (full.Items || []).forEach(function (item) {
                    itemsById[item.Id] = item;
                });
                renderResults(hints.map(function (h) {
                    return itemsById[h.ItemId || h.Id];
                }).filter(Boolean));
            });
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
            url: ApiClient.getUrl('Subsync/Sync/' + itemId),
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

    function fetchSubtitleCandidates(itemId) {
        return ApiClient.ajax({
            type: 'GET',
            url: ApiClient.getUrl('Subsync/Sync/' + itemId + '/Subtitles'),
            dataType: 'json'
        });
    }

    // Rebuilds the reference `<select>` from whichever subtitle is
    // currently chosen as the sync target, excluding that one - the
    // client-side mirror of the rule the endpoint itself enforces (a
    // subtitle can't be synced against itself).
    function refreshReferenceOptions(panel) {
        const targetSelect = panel.querySelector('.subtitleTargetSelect');
        const referenceSelect = panel.querySelector('.subtitleReferenceSelect');
        if (!targetSelect || !referenceSelect) return;

        const row = panel.closest('.itemResultRow');
        referenceSelect.innerHTML = buildReferenceOptionsHtml(row._subtitles || [], Number(targetSelect.value));
    }

    function toggleSubtitlePanel(row) {
        const panel = row.querySelector('.itemResultSubtitlePanel');

        if (!panel.hidden) {
            panel.hidden = true;
            return;
        }

        panel.hidden = false;

        // Fetched once per row and cached on the element - re-expanding an
        // already-loaded row shouldn't re-fetch.
        if (row._subtitles) return;

        panel.innerHTML = '<div class="fieldDescription">Loading subtitles…</div>';

        fetchSubtitleCandidates(row.dataset.itemId).then(function (data) {
            row._subtitles = data.subtitles || [];
            panel.innerHTML = buildSubtitlePanelHtml(data);
            refreshReferenceOptions(panel);
        }).catch(function () {
            row._subtitles = null;
            panel.innerHTML = '<div class="fieldDescription">Failed to load subtitles - try again.</div>';
        });
    }

    function syncOneSubtitle(row) {
        const panel = row.querySelector('.itemResultSubtitlePanel');
        const targetSelect = panel.querySelector('.subtitleTargetSelect');
        const referenceSelect = panel.querySelector('.subtitleReferenceSelect');
        const button = panel.querySelector('.syncOneSubtitleButton');
        const status = panel.querySelector('.syncOneStatus');
        if (!targetSelect || !referenceSelect) return;

        const itemId = row.dataset.itemId;
        const body = {
            subtitleIndex: Number(targetSelect.value),
            referenceSubtitleIndex: referenceSelect.value === '' ? null : Number(referenceSelect.value)
        };

        button.disabled = true;
        status.textContent = 'Syncing…';

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('Subsync/Sync/' + itemId),
            data: JSON.stringify(body),
            contentType: 'application/json',
            dataType: 'json'
        }).then(function (result) {
            button.disabled = false;
            status.textContent = renderSyncSummary(result);
        }).catch(function (err) {
            button.disabled = false;
            if (err && err.status === 409) {
                status.textContent = 'A library sweep is currently running - try again once it finishes.';
            } else if (err && err.status === 400) {
                status.textContent = 'Failed to sync - the selected subtitle or reference is no longer available.';
            } else {
                status.textContent = 'Failed to sync - try again';
            }
        });
    }

    view.addEventListener('viewshow', function () {
        LibraryMenu.setTabs('subsync', 2, getTabs);

        byId('ItemSearch').value = '';
        const results = byId('ItemSearchResults');
        results.innerHTML = '';
        results.style.borderTop = '';
    });

    byId('ItemSearch').addEventListener('input', function () {
        const term = this.value.trim();
        if (searchTimer) window.clearTimeout(searchTimer);
        searchTimer = window.setTimeout(function () {
            searchItems(term);
        }, SEARCH_DEBOUNCE_MS);
    });

    byId('ItemSearchResults').addEventListener('click', function (e) {
        const syncButton = e.target.closest('.syncItemButton');
        if (syncButton) {
            syncItem(syncButton.closest('.itemResultRow'));
            return;
        }

        const toggleButton = e.target.closest('.syncOneToggle');
        if (toggleButton) {
            toggleSubtitlePanel(toggleButton.closest('.itemResultRow'));
            return;
        }

        const syncOneButton = e.target.closest('.syncOneSubtitleButton');
        if (syncOneButton) {
            syncOneSubtitle(syncOneButton.closest('.itemResultRow'));
        }
    });

    byId('ItemSearchResults').addEventListener('change', function (e) {
        if (!e.target.classList.contains('subtitleTargetSelect')) return;
        refreshReferenceOptions(e.target.closest('.itemResultSubtitlePanel'));
    });
}
