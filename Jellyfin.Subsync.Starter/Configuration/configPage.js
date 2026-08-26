'use strict';

const pluginId = '6e9cb927-95fc-4ab9-8267-c896060ae50e';
const MAX_PARALLEL_JOBS_CEILING = 32; // fixed, reasonable upper bound

function arrayToCsv(arr) {
    return (arr || []).join(',');
}

function csvToArray(text) {
    return text.split(',').map(function (s) {
        return s.trim();
    }).filter(Boolean);
}

function stripTrailingSlash(path) {
    return (path || '').replace(/\/+$/, '');
}

function sameGuid(a, b) {
    return (a || '').toLowerCase() === (b || '').toLowerCase();
}

const htmlEscapes = {'&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'};

function escapeHtml(text) {
    return (text || '').replace(/[&<>"']/g, function (c) {
        return htmlEscapes[c];
    });
}

// Only used when config.LibraryPathMappings is empty (i.e. this
// install hasn't been through the new UI yet), to pre-fill the
// form from the old free-text WatchedPathsMaps. Purely in-memory
// - nothing is persisted until Save. Matches on exact path
// equality (trailing-slash-insensitive) only: looser prefix
// matching risks picking the wrong SidecarPath when a library
// has multiple locations under one old parent entry.
function migrateFromWatchedPathsMaps(oldEntries, libraries) {
    return libraries.map(function (library) {
        const pathMappings = [];
        let anyMatch = false;
        (library.Locations || []).forEach(function (location) {
            const oldEntry = (oldEntries || []).find(function (e) {
                return stripTrailingSlash(e.JellyfinPath) === stripTrailingSlash(location);
            });
            if (oldEntry) {
                anyMatch = true;
            }
            pathMappings.push({JellyfinPath: location, SidecarPath: oldEntry ? oldEntry.SidecarPath : ''});
        });
        return {
            LibraryId: library.ItemId,
            LibraryName: library.Name,
            Enabled: anyMatch,
            PathMappings: pathMappings
        };
    });
}

function buildLibraryBlockHtml(library, existing) {
    const checked = existing && existing.Enabled ? ' checked' : '';
    const rows = (library.Locations || []).map(function (location) {
        const priorEntry = existing && existing.PathMappings.find(function (e) {
            return e.JellyfinPath === location;
        });
        const value = priorEntry ? priorEntry.SidecarPath : '';
        return '' +
            '<div class="inputContainer locationRow" data-jellyfin-path="' + escapeHtml(location) + '">' +
            '<label class="inputLabel inputLabelUnfocused">' + escapeHtml(location) + '</label>' +
            '<input is="emby-input" type="text" class="sidecarPathInput" value="' + escapeHtml(value) + '" ' +
            'placeholder="Sidecar-side equivalent, e.g. /mnt/media/Movies" />' +
            '</div>';
    }).join('');

    return '' +
        '<div class="verticalSection libraryMapping" data-library-id="' + escapeHtml(library.ItemId) + '">' +
        '<label>' +
        '<input is="emby-checkbox" type="checkbox" class="libraryEnabled"' + checked + ' />' +
        '<span class="libraryNameText">' + escapeHtml(library.Name) + '</span>' +
        '</label>' +
        '<div class="libraryLocations" style="margin-left:2em;">' + rows + '</div>' +
        '</div>';
}

// Shared between this page and the Cache page so both render the same tab
// strip via LibraryMenu.setTabs - only the active index differs per page.
function getTabs() {
    return [
        {href: Dashboard.getPluginUrl('Subsync'), name: 'Settings'},
        {href: Dashboard.getPluginUrl('Cache'), name: 'Cache'}
    ];
}

export default function (view) {
    function byId(id) {
        return view.querySelector('#' + id);
    }

    // Falls back to `fallback` when the field is blank/non-numeric or
    // below `min`. A plain `|| fallback` would treat a deliberate 0 as
    // blank, which matters for fields where 0 is a legitimate value.
    function getIntValue(id, fallback, min) {
        const parsed = parseInt(byId(id).value, 10);
        if (isNaN(parsed) || (typeof min === 'number' && parsed < min)) {
            return fallback;
        }
        return parsed;
    }

    function renderLibraryList(libraries, existingMappings) {
        byId('LibraryList').innerHTML = libraries.map(function (library) {
            const existing = existingMappings.find(function (m) {
                return sameGuid(m.LibraryId, library.ItemId);
            });
            return buildLibraryBlockHtml(library, existing);
        }).join('');
    }

    function collectLibraryPathMappings() {
        return Array.prototype.map.call(byId('LibraryList').querySelectorAll('.libraryMapping'), function (block) {
            return {
                LibraryId: block.dataset.libraryId,
                LibraryName: block.querySelector('.libraryNameText').textContent,
                Enabled: block.querySelector('.libraryEnabled').checked,
                PathMappings: Array.prototype.map.call(block.querySelectorAll('.locationRow'), function (row) {
                    return {
                        JellyfinPath: row.dataset.jellyfinPath,
                        SidecarPath: row.querySelector('.sidecarPathInput').value.trim()
                    };
                })
            };
        });
    }

    function setUpMaxParallelJobsSlider(currentValue) {
        const slider = byId('MaxParallelJobs');
        const readout = byId('MaxParallelJobsValue');
        slider.value = String(Math.min(Math.max(currentValue, 1), MAX_PARALLEL_JOBS_CEILING));
        readout.textContent = slider.value;
        slider.addEventListener('input', function () {
            readout.textContent = slider.value;
        });
    }

    view.addEventListener('viewshow', function () {
        LibraryMenu.setTabs('subsync', 0, getTabs);

        Dashboard.showLoadingMsg();
        Promise.all([
            ApiClient.getPluginConfiguration(pluginId),
            ApiClient.getVirtualFolders()
        ]).then(function (results) {
            const config = results[0];
            const libraries = results[1];

            byId('SidecarUrl').value = config.SidecarUrl || '';
            byId('SubtitleExtensions').value = arrayToCsv(config.SubtitleExtensions);
            byId('PollIntervalMilliseconds').value = config.PollIntervalMilliseconds || 3000;
            byId('JobTimeoutSeconds').value = config.JobTimeoutSeconds || 1800;
            // Not `|| 3600`: 0 is a legitimate value here (wait
            // indefinitely) and would otherwise be shown as an hour.
            byId('QueueWaitTimeoutSeconds').value =
                typeof config.QueueWaitTimeoutSeconds === 'number' ? config.QueueWaitTimeoutSeconds : 3600;
            byId('SidecarRequestTimeoutSeconds').value = config.SidecarRequestTimeoutSeconds || 30;

            const effectiveMappings = (config.LibraryPathMappings && config.LibraryPathMappings.length > 0)
                ? config.LibraryPathMappings
                : migrateFromWatchedPathsMaps(config.WatchedPathsMaps, libraries);
            renderLibraryList(libraries, effectiveMappings);

            setUpMaxParallelJobsSlider(config.MaxParallelJobs || 1);

            Dashboard.hideLoadingMsg();
        });
    });

    byId('SubsyncConfigForm').addEventListener('submit', function (e) {
        e.preventDefault();
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            config.SidecarUrl = byId('SidecarUrl').value.trim();
            config.LibraryPathMappings = collectLibraryPathMappings();
            // WatchedPathsMaps is intentionally left untouched here -
            // the server derives it from LibraryPathMappings on save.
            config.SubtitleExtensions = csvToArray(byId('SubtitleExtensions').value);
            config.PollIntervalMilliseconds = getIntValue('PollIntervalMilliseconds', 3000, 1);
            config.JobTimeoutSeconds = getIntValue('JobTimeoutSeconds', 1800, 1);
            // Same reason as on load: a deliberate 0 ("wait
            // indefinitely") must not be replaced with the fallback.
            config.QueueWaitTimeoutSeconds = getIntValue('QueueWaitTimeoutSeconds', 3600, 0);
            config.SidecarRequestTimeoutSeconds = getIntValue('SidecarRequestTimeoutSeconds', 30, 1);
            config.MaxParallelJobs = parseInt(byId('MaxParallelJobs').value, 10) || 1;

            ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
                Dashboard.processPluginConfigurationUpdateResult(result);
            });
        });
        return false;
    });
}
