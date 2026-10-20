export default function (view) {
    'use strict';

    var pluginId = '1278dbd6-8399-46c0-8e64-31f2d9e022ae';

    // ── 1. Telemetry Metric DOM references ──
    var metricCircuitState = view.querySelector('#metricCircuitState');
    var metricCircuitSub = view.querySelector('#metricCircuitSub');
    var metricCooldownTimer = view.querySelector('#metricCooldownTimer');
    var metricCooldownSub = view.querySelector('#metricCooldownSub');
    var metricQueueDepth = view.querySelector('#metricQueueDepth');
    var metricQueueSub = view.querySelector('#metricQueueSub');
    var metricActiveProbes = view.querySelector('#metricActiveProbes');
    var metricActiveProbesSub = view.querySelector('#metricActiveProbesSub');
    var circuitBanner = view.querySelector('#circuitBreakerBanner');
    var circuitStatus = view.querySelector('#circuitBreakerStatus');

    // ── 2. Settings Form DOM references ──
    var form = view.querySelector('.quickStrmConfigurationForm');
    var chkAutoProbe = view.querySelector('#chkEnableAutoProbe');
    var chkAutoDelete = view.querySelector('#chkEnableAutoDelete');
    var chkNextEp = view.querySelector('#chkEnableNextEpisodePreWarm');
    var chkChapters = view.querySelector('#chkSynthesizeVirtualChapters');
    var txtMarker = view.querySelector('#txtMountMarkerFileName');
    var txtJellyfinUrl = view.querySelector('#txtJellyfinBaseUrl');

    // ── 3. *arr Suite DOM references ──
    var btnSyncArr = view.querySelector('#btnSyncArrWebhooks');
    var btnWipeArr = view.querySelector('#btnWipeArrWebhooks');
    var btnShowAddModal = view.querySelector('#btnShowAddArrModal');
    var arrList = view.querySelector('#arrInstancesList');
    var addPanel = view.querySelector('#arrAddInstancePanel');
    var addName = view.querySelector('#addArrName');
    var addType = view.querySelector('#addArrType');
    var addBaseUrl = view.querySelector('#addArrBaseUrl');
    var addApiKey = view.querySelector('#addArrApiKey');
    var addPathFrom = view.querySelector('#addArrPathFrom');
    var addPathTo = view.querySelector('#addArrPathTo');
    var addAutoHeal = view.querySelector('#addArrAutoHeal');
    var btnSaveAdd = view.querySelector('#btnSaveArrInstance');
    var btnCancelAdd = view.querySelector('#btnCancelArrInstance');

    // ── 4. STRM Tools DOM references ──
    var txtRewriteOldPrefix = view.querySelector('#txtRewriteOldPrefix');
    var txtRewriteNewPrefix = view.querySelector('#txtRewriteNewPrefix');
    var chkRewriteDryRun = view.querySelector('#chkRewriteDryRun');
    var btnRunRewrite = view.querySelector('#btnRunRewrite');
    var rewriteResults = view.querySelector('#rewriteResults');

    // Pre-probe & Failure cache DOM references
    var lblTotal = view.querySelector('#lblSummaryTotal');
    var lblProbed = view.querySelector('#lblSummaryProbed');
    var lblUnprobed = view.querySelector('#lblSummaryUnprobed');
    var lblQueue = view.querySelector('#lblSummaryQueue');
    var lblQueueDetail = view.querySelector('#lblSummaryQueueDetail');
    var selectMode = view.querySelector('#selectProbeMode');
    var btnStart = view.querySelector('#btnStartProbe');
    var btnStop = view.querySelector('#btnStopProbe');
    var btnResetFailureCache = view.querySelector('#btnResetFailureCache');
    var lblStatus = view.querySelector('#lblTaskStatus');
    var progressBar = view.querySelector('#probeProgressBar');
    var lblCurrentItem = view.querySelector('#lblCurrentItem');

    var pollTimer = null;
    var queuePollTimer = null;
    var countdownTimer = null;
    var cooldownEndsAt = 0;
    var circuitState = 'Closed';

    // ── Utility Functions ──

    function escapeHtml(str) {
        if (!str) { return ''; }
        return String(str)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#039;');
    }

    function extractErrorMessage(err) {
        if (!err) {
            return 'Unknown error occurred.';
        }
        if (typeof err === 'string') {
            return err;
        }
        if (err.message) {
            return err.message;
        }
        if (err.statusText) {
            return err.statusText + (err.status ? ' (' + err.status + ')' : '');
        }
        if (err.status) {
            return 'HTTP error ' + err.status;
        }
        try {
            return JSON.stringify(err);
        } catch (e) {
            return String(err);
        }
    }

    function showLoading() {
        if (typeof Dashboard !== 'undefined' && typeof Dashboard.showLoadingMsg === 'function') {
            Dashboard.showLoadingMsg();
        }
    }

    function hideLoading() {
        if (typeof Dashboard !== 'undefined' && typeof Dashboard.hideLoadingMsg === 'function') {
            Dashboard.hideLoadingMsg();
        }
    }

    function showAlert(msg) {
        if (typeof Dashboard !== 'undefined' && typeof Dashboard.alert === 'function') {
            Dashboard.alert({ message: msg });
        } else {
            alert(msg);
        }
    }

    function confirmDialog(message, title) {
        return new Promise(function (resolve) {
            if (typeof Dashboard !== 'undefined' && typeof Dashboard.confirm === 'function') {
                try {
                    var p = Dashboard.confirm(message, title || 'Confirmation');
                    if (p && typeof p.then === 'function') {
                        p.then(function () {
                            resolve(true);
                        }).catch(function () {
                            resolve(false);
                        });
                        return;
                    }
                } catch (e) {
                    // Fallback to window.confirm below
                }
            }
            resolve(window.confirm(message));
        });
    }

    function callApiFetch(endpoint, method, data) {
        var url = ApiClient.getUrl(endpoint);
        var req = {
            url: url,
            type: method || 'GET',
            dataType: 'json'
        };

        if (typeof data !== 'undefined') {
            req.data = typeof data === 'string' ? data : JSON.stringify(data);
            req.contentType = 'application/json';
        }

        var fetchPromise;
        if (typeof ApiClient.fetch === 'function') {
            fetchPromise = ApiClient.fetch(req);
        } else if (typeof ApiClient.ajax === 'function') {
            fetchPromise = ApiClient.ajax(req);
        } else {
            fetchPromise = window.fetch(url, {
                method: req.type,
                headers: {
                    'Content-Type': 'application/json',
                    'X-Emby-Authorization': ApiClient.authorizationHeader ? ApiClient.authorizationHeader() : ''
                },
                body: req.data
            });
        }

        return fetchPromise.then(function (response) {
            if (response && typeof response.ok !== 'undefined') {
                if (!response.ok) {
                    var statusInfo = response.statusText ? (response.statusText + ' (' + response.status + ')') : ('HTTP ' + response.status);
                    var textPromise = typeof response.text === 'function' ? response.text() : Promise.resolve('');
                    return textPromise.then(function (body) {
                        var errorMsg = body && body.trim().length > 0 ? body : statusInfo;
                        try {
                            var parsed = JSON.parse(body);
                            if (parsed && (parsed.message || parsed.Message)) {
                                errorMsg = parsed.message || parsed.Message;
                            }
                        } catch (e) {
                            // Non-JSON body
                        }
                        var err = new Error(errorMsg);
                        err.status = response.status;
                        err.statusText = response.statusText;
                        throw err;
                    });
                }
                if (typeof response.json === 'function') {
                    return response.json().catch(function () {
                        return null;
                    });
                }
            }
            return response;
        });
    }

    function apiGet(path) {
        return callApiFetch(path, 'GET');
    }

    function apiPost(path, data) {
        return callApiFetch(path, 'POST', data);
    }

    // ── Real-time Circuit & Telemetry Rendering ──

    function renderCircuit() {
        var left = Math.max(0, Math.ceil((cooldownEndsAt - Date.now()) / 1000));
        var m = Math.floor(left / 60);
        var s = left % 60;
        var timeStr = m + ':' + (s < 10 ? '0' : '') + s;

        if (circuitState === 'Closed') {
            if (circuitBanner) { circuitBanner.style.display = 'none'; }
            if (metricCircuitState) {
                metricCircuitState.textContent = 'Closed';
                metricCircuitState.style.color = '#4caf50';
            }
            if (metricCircuitSub) { metricCircuitSub.textContent = 'Normal Operation'; }
            if (metricCooldownTimer) {
                metricCooldownTimer.textContent = '0:00';
                metricCooldownTimer.style.color = 'var(--text-muted-color, #888)';
            }
            if (metricCooldownSub) { metricCooldownSub.textContent = 'Inactive'; }
            return;
        }

        if (circuitBanner) {
            circuitBanner.style.display = 'block';
        }

        if (circuitState === 'Open' && left > 0) {
            if (circuitStatus) {
                circuitStatus.textContent = 'Auto-recovery: next connectivity test in ' + timeStr + '.';
            }
            if (metricCircuitState) {
                metricCircuitState.textContent = 'Open';
                metricCircuitState.style.color = '#e53935';
            }
            if (metricCircuitSub) {
                metricCircuitSub.textContent = 'Mount Offline / Cooldown';
            }
            if (metricCooldownTimer) {
                metricCooldownTimer.textContent = timeStr;
                metricCooldownTimer.style.color = '#e53935';
            }
            if (metricCooldownSub) {
                metricCooldownSub.textContent = 'Cooldown Active';
            }
        } else {
            if (circuitStatus) {
                circuitStatus.textContent = 'Auto-recovery: testing connectivity now (half-open). One probe is being allowed through.';
            }
            if (metricCircuitState) {
                metricCircuitState.textContent = 'Half-Open';
                metricCircuitState.style.color = '#ff9800';
            }
            if (metricCircuitSub) {
                metricCircuitSub.textContent = 'Trial In Flight';
            }
            if (metricCooldownTimer) {
                metricCooldownTimer.textContent = 'Testing';
                metricCooldownTimer.style.color = '#ff9800';
            }
            if (metricCooldownSub) {
                metricCooldownSub.textContent = 'Single Probe Trial';
            }
        }
    }

    function startCountdown() {
        if (!countdownTimer) {
            countdownTimer = setInterval(renderCircuit, 1000);
        }
    }

    function stopCountdown() {
        if (countdownTimer) {
            clearInterval(countdownTimer);
            countdownTimer = null;
        }
    }

    // ── Data Loading ──

    function loadSummary() {
        apiGet('QuickStrm/Summary').then(function (data) {
            if (lblTotal) { lblTotal.textContent = data.Total; }
            if (lblProbed) { lblProbed.textContent = data.Probed; }
            if (lblUnprobed) { lblUnprobed.textContent = data.Unprobed; }
        });
    }

    function loadQueue() {
        apiGet('QuickStrm/Queue').then(function (data) {
            var waiting = data.Waiting || 0;
            var running = data.Running || 0;
            var high = data.HighPriorityWaiting || 0;
            var low = data.LowPriorityWaiting || 0;

            if (lblQueue) { lblQueue.textContent = waiting + running; }
            if (lblQueueDetail) { lblQueueDetail.textContent = waiting + ' waiting / ' + running + ' running'; }

            if (metricQueueDepth) {
                metricQueueDepth.textContent = high + ' / ' + low;
            }
            if (metricQueueSub) {
                metricQueueSub.textContent = high + ' High / ' + low + ' Low Lane';
            }

            if (metricActiveProbes) {
                metricActiveProbes.textContent = running + ' / 2';
            }
            if (metricActiveProbesSub) {
                var cacheCount = typeof data.CacheSize !== 'undefined' ? data.CacheSize : 0;
                metricActiveProbesSub.textContent = cacheCount === 1 ? '1 failed item in cache' : cacheCount + ' failed items in cache';
            }

            circuitState = data.CircuitState || (data.CircuitTripped ? 'Open' : 'Closed');
            cooldownEndsAt = Date.now() + (data.CooldownSecondsRemaining || 0) * 1000;

            renderCircuit();
            startCountdown();
            startQueuePolling();
        });
    }

    function loadStatus() {
        apiGet('QuickStrm/Probe/Status').then(function (data) {
            var stateMap = { Idle: 'Idle', Running: 'Running', Stopping: 'Stopping' };
            if (lblStatus) { lblStatus.textContent = 'Status: ' + (stateMap[data.State] || data.State); }
            if (lblCurrentItem) { lblCurrentItem.textContent = data.CurrentItem || '-'; }

            var pct = data.Total > 0 ? Math.round((data.Processed / data.Total) * 100) : 0;
            if (progressBar) { progressBar.style.width = pct + '%'; }

            if (data.State === 'Running' || data.State === 'Stopping') {
                if (btnStart) { btnStart.style.display = 'none'; }
                if (btnStop) { btnStop.style.display = 'inline-flex'; }
                startPolling();
            } else {
                if (btnStart) { btnStart.style.display = 'inline-flex'; }
                if (btnStop) { btnStop.style.display = 'none'; }
                stopPolling();
                loadSummary();
            }
        });
    }

    // ── *arr Suite Rendering ──

    function renderArrInstances(instances, defaultUnsyncedText) {
        if (!arrList) { return; }
        if (!instances || instances.length === 0) {
            arrList.innerHTML = '<div style="color: var(--text-muted-color, #777); font-size: 0.9em; padding: 1em 0;">No *arr instances configured. Click "+ Add Instance" above to connect.</div>';
            return;
        }

        var unsyncedLabel = defaultUnsyncedText || 'Unsynced/Error';
        var html = '';
        instances.forEach(function (inst, index) {
            var isSynced = inst.ProvisionedWebhookId != null && inst.ProvisionedWebhookId > 0;
            var badgeText = isSynced ? 'Synced' : unsyncedLabel;
            var badgeClass = isSynced
                ? 'badge sync-status synced quickstrm-badge quickstrm-badge-green'
                : 'badge sync-status unsynced quickstrm-badge quickstrm-badge-red';

            var webhookBadge = '<span class="' + badgeClass + '" data-index="' + index + '">' +
                escapeHtml(badgeText) +
                '</span>';

            var healBadge = inst.AutoHealOn404
                ? '<span class="quickstrm-badge quickstrm-badge-green" style="margin-left: 0.4em;">Auto-Heal Active</span>'
                : '<span class="quickstrm-badge" style="background: rgba(255,255,255,0.06); color: #888; margin-left: 0.4em;">Auto-Heal Off</span>';

            var pathMapping = (inst.PathFrom || '(none)') + ' &rarr; ' + (inst.PathTo || '(none)');

            html += '<div class="quickstrm-arr-card" data-index="' + index + '">' +
                '<div style="flex: 1; min-width: 260px;">' +
                    '<div style="display: flex; align-items: center; flex-wrap: wrap; gap: 0.4em; margin-bottom: 0.35em;">' +
                        '<span style="font-weight: 600; font-size: 1.05em; color: var(--text-color, #fff);">' + escapeHtml(inst.Name || 'Unnamed') + '</span>' +
                        '<span class="quickstrm-badge quickstrm-badge-type">' + escapeHtml(inst.ArrType || 'Radarr') + '</span>' +
                        webhookBadge +
                        healBadge +
                    '</div>' +
                    '<div style="font-size: 0.82em; color: var(--text-muted-color, #888); word-break: break-all;">' +
                        '<span>' + escapeHtml(inst.BaseUrl || '') + '</span>' +
                        '<span style="margin: 0 0.5em; opacity: 0.5;">|</span>' +
                        '<span>Path: ' + escapeHtml(pathMapping) + '</span>' +
                        (inst.ProvisionedWebhookId ? '<span style="margin: 0 0.5em; opacity: 0.5;">|</span><span>Hook #' + escapeHtml(inst.ProvisionedWebhookId) + '</span>' : '') +
                    '</div>' +
                '</div>' +
                '<div style="display: flex; gap: 0.5em; align-items: center;">' +
                    '<button is="emby-button" type="button" class="raised emby-button btn-remove-arr" data-index="' + index + '" style="background: #b71c1c; color: white; padding: 0.4em 0.8em; font-size: 0.85em; margin: 0;">' +
                        '<span>Remove</span>' +
                    '</button>' +
                '</div>' +
            '</div>';
        });

        arrList.innerHTML = html;

        var removeBtns = arrList.querySelectorAll('.btn-remove-arr');
        removeBtns.forEach(function (btn) {
            btn.addEventListener('click', function () {
                var idx = parseInt(btn.getAttribute('data-index'), 10);
                removeArrInstance(idx);
            });
        });
    }

    function updateBadgesFromResults(results) {
        if (!Array.isArray(results) || !arrList) { return; }
        var cards = arrList.querySelectorAll('.quickstrm-arr-card');
        cards.forEach(function (card, index) {
            var badge = card.querySelector('.sync-status');
            if (!badge) { return; }
            var res = results[index];
            if (res && (res.Success || res.success) && (res.WebhookId || res.webhookId)) {
                badge.className = 'badge sync-status synced quickstrm-badge quickstrm-badge-green';
                badge.textContent = 'Synced';
            } else {
                badge.className = 'badge sync-status unsynced quickstrm-badge quickstrm-badge-red';
                badge.textContent = 'Unsynced/Error';
            }
        });
    }

    function forceBadgesUnsynced() {
        if (!arrList) { return; }
        var badges = arrList.querySelectorAll('.sync-status');
        badges.forEach(function (badge) {
            badge.className = 'badge sync-status unsynced quickstrm-badge quickstrm-badge-red';
            badge.textContent = 'Unsynced';
        });
    }

    function removeArrInstance(index) {
        confirmDialog('Are you sure you want to remove this *arr connection?', 'Remove Connection').then(function (confirmed) {
            if (!confirmed) { return; }

            showLoading();
            ApiClient.getPluginConfiguration(pluginId).then(function (config) {
                var instances = config.ArrInstances || [];
                var removed = instances.splice(index, 1)[0];
                config.ArrInstances = instances;
                return ApiClient.updatePluginConfiguration(pluginId, config).then(function () {
                    if (removed && removed.ProvisionedWebhookId) {
                        return apiPost('QuickStrm/Arr/TestAndSync').catch(function () {
                            // ignore errors on sync during remove
                        });
                    }
                });
            }).then(function () {
                hideLoading();
                return loadConfig();
            }).catch(function (err) {
                hideLoading();
                showAlert('Failed to remove connection: ' + extractErrorMessage(err));
                loadConfig();
            });
        });
    }

    function clearAddForm() {
        if (addName) { addName.value = ''; }
        if (addType) { addType.value = 'Radarr'; }
        if (addBaseUrl) { addBaseUrl.value = ''; }
        if (addApiKey) { addApiKey.value = ''; }
        if (addPathFrom) { addPathFrom.value = ''; }
        if (addPathTo) { addPathTo.value = ''; }
        if (addAutoHeal) { addAutoHeal.checked = true; }
    }

    function loadConfig(defaultUnsyncedText) {
        return ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            if (chkAutoProbe) { chkAutoProbe.checked = !!config.EnableAutoProbe; }
            if (chkAutoDelete) { chkAutoDelete.checked = !!config.EnableAutoDelete; }
            if (chkNextEp) { chkNextEp.checked = config.EnableNextEpisodePreWarm !== false; }
            if (chkChapters) { chkChapters.checked = config.SynthesizeVirtualChapters !== false; }
            if (txtMarker) { txtMarker.value = config.MountMarkerFileName || ''; }
            if (txtJellyfinUrl) { txtJellyfinUrl.value = config.JellyfinBaseUrl || ''; }

            renderArrInstances(config.ArrInstances || [], defaultUnsyncedText);
            return config;
        });
    }

    // ── Polling Management ──

    function startPolling() {
        if (!pollTimer) {
            pollTimer = setInterval(loadStatus, 1500);
        }
    }

    function stopPolling() {
        if (pollTimer) {
            clearInterval(pollTimer);
            pollTimer = null;
        }
    }

    function startQueuePolling() {
        if (!queuePollTimer) {
            queuePollTimer = setInterval(loadQueue, 3000);
        }
    }

    function stopQueuePolling() {
        if (queuePollTimer) {
            clearInterval(queuePollTimer);
            queuePollTimer = null;
        }
    }

    // ── Event Handlers ──

    // 1. Settings Form Submit
    if (form) {
        form.addEventListener('submit', function (e) {
            e.preventDefault();
            ApiClient.getPluginConfiguration(pluginId).then(function (config) {
                config.EnableAutoProbe = chkAutoProbe ? chkAutoProbe.checked : false;
                config.EnableAutoDelete = chkAutoDelete ? chkAutoDelete.checked : false;
                config.EnableNextEpisodePreWarm = chkNextEp ? chkNextEp.checked : true;
                config.SynthesizeVirtualChapters = chkChapters ? chkChapters.checked : true;
                config.MountMarkerFileName = txtMarker ? txtMarker.value.trim() : '';
                config.JellyfinBaseUrl = txtJellyfinUrl ? txtJellyfinUrl.value.trim() : '';

                ApiClient.updatePluginConfiguration(pluginId, config).then(function () {
                    Dashboard.processPluginConfigurationUpdateResult();
                });
            });
        });
    }

    // 2. Add *arr Instance Modal / Controls
    if (btnShowAddModal) {
        btnShowAddModal.addEventListener('click', function () {
            if (addPanel) {
                addPanel.style.display = addPanel.style.display === 'none' ? 'block' : 'none';
            }
        });
    }

    if (btnCancelAdd) {
        btnCancelAdd.addEventListener('click', function () {
            if (addPanel) {
                addPanel.style.display = 'none';
            }
            clearAddForm();
        });
    }

    if (btnSaveAdd) {
        btnSaveAdd.addEventListener('click', function () {
            var name = addName ? addName.value.trim() : '';
            var type = addType ? addType.value : 'Radarr';
            var baseUrl = addBaseUrl ? addBaseUrl.value.trim() : '';
            var apiKey = addApiKey ? addApiKey.value.trim() : '';
            var pathFrom = addPathFrom ? addPathFrom.value.trim() : '';
            var pathTo = addPathTo ? addPathTo.value.trim() : '';
            var autoHeal = addAutoHeal ? addAutoHeal.checked : true;

            if (!name || !baseUrl || !apiKey) {
                showAlert('Please specify Instance Name, Base URL, and API Key.');
                return;
            }

            btnSaveAdd.disabled = true;
            showLoading();

            ApiClient.getPluginConfiguration(pluginId).then(function (config) {
                config.ArrInstances = config.ArrInstances || [];
                config.ArrInstances.push({
                    Name: name,
                    ArrType: type,
                    BaseUrl: baseUrl,
                    ApiKey: apiKey,
                    PathFrom: pathFrom,
                    PathTo: pathTo,
                    AutoHealOn404: autoHeal,
                    ProvisionedWebhookId: null
                });

                return ApiClient.updatePluginConfiguration(pluginId, config).then(function () {
                    btnSaveAdd.disabled = false;
                    clearAddForm();
                    if (addPanel) {
                        addPanel.style.display = 'none';
                    }
                    return apiPost('QuickStrm/Arr/TestAndSync');
                });
            }).then(function () {
                hideLoading();
                loadConfig();
            }).catch(function (err) {
                btnSaveAdd.disabled = false;
                hideLoading();
                showAlert('Failed to save connection: ' + extractErrorMessage(err));
                loadConfig();
            });
        });
    }

    // 3. *arr Sync & Wipe Webhook Buttons
    if (btnSyncArr) {
        btnSyncArr.addEventListener('click', function () {
            btnSyncArr.disabled = true;
            showLoading();

            apiPost('QuickStrm/Arr/TestAndSync')
                .then(function (results) {
                    btnSyncArr.disabled = false;
                    hideLoading();

                    updateBadgesFromResults(results);

                    var failures = [];
                    if (Array.isArray(results)) {
                        results.forEach(function (r) {
                            var ok = r.Success || r.success;
                            if (!ok) {
                                var name = r.InstanceName || r.instanceName || r.Name || r.name || 'Instance';
                                var msg = r.Message || r.message || 'Sync failed';
                                failures.push(name + ': ' + msg);
                            }
                        });
                    }

                    loadConfig();

                    if (failures.length > 0) {
                        var errorMsg = 'Webhook synchronization failed:\n' + failures.join('\n');
                        showAlert(errorMsg);
                    }
                })
                .catch(function (err) {
                    btnSyncArr.disabled = false;
                    hideLoading();
                    var errMsg = extractErrorMessage(err);
                    showAlert('Error syncing webhooks: ' + errMsg);
                    loadConfig();
                });
        });
    }

    if (btnWipeArr) {
        btnWipeArr.addEventListener('click', function () {
            confirmDialog(
                "This will remove Jellyfin's hooks from Radarr/Sonarr/Whisparr. Are you sure you want to proceed?",
                "Wipe All *arr Webhooks"
            ).then(function (confirmed) {
                if (!confirmed) {
                    return;
                }

                btnWipeArr.disabled = true;
                showLoading();

                apiPost('QuickStrm/Arr/WipeWebhooks')
                    .then(function (results) {
                        btnWipeArr.disabled = false;
                        hideLoading();
                        forceBadgesUnsynced();
                        loadConfig('Unsynced');
                    })
                    .catch(function (err) {
                        btnWipeArr.disabled = false;
                        hideLoading();
                        var errMsg = extractErrorMessage(err);
                        showAlert('Error wiping webhooks: ' + errMsg);
                        loadConfig();
                    });
            });
        });
    }

    // 4. STRM Bulk URL Rewriter
    if (btnRunRewrite) {
        btnRunRewrite.addEventListener('click', function () {
            var oldPrefix = txtRewriteOldPrefix ? txtRewriteOldPrefix.value.trim() : '';
            var newPrefix = txtRewriteNewPrefix ? txtRewriteNewPrefix.value.trim() : '';
            var dryRun = chkRewriteDryRun ? chkRewriteDryRun.checked : true;

            if (!oldPrefix || !newPrefix) {
                showAlert('Please specify both Old Prefix and New Prefix.');
                return;
            }

            var confirmPromise = dryRun
                ? Promise.resolve(true)
                : confirmDialog('Are you sure you want to execute this rewrite? This will modify matching .strm files on disk.', 'Confirm Rewrite');

            confirmPromise.then(function (confirmed) {
                if (!confirmed) {
                    return;
                }

                btnRunRewrite.disabled = true;
                if (rewriteResults) {
                    rewriteResults.style.display = 'block';
                    rewriteResults.innerHTML = 'Scanning and rewriting .strm files...';
                }

                callApiFetch('QuickStrm/Tools/RewriteUrls', 'POST', {
                    OldPrefix: oldPrefix,
                    NewPrefix: newPrefix,
                    DryRun: dryRun
                }).then(function (res) {
                    btnRunRewrite.disabled = false;
                    if (rewriteResults) {
                        var out = '<div><strong>Status:</strong> ' + (res.DryRun ? '<span style="color: #ff9800;">DRY RUN (No files modified)</span>' : '<span style="color: #4caf50;">COMPLETE</span>') + '</div>';
                        out += '<div><strong>Total Scanned:</strong> ' + res.TotalScanned + '</div>';
                        out += '<div><strong>Matches Found:</strong> ' + res.MatchesFound + '</div>';
                        if (!res.DryRun) {
                            out += '<div><strong>Files Modified:</strong> ' + res.Modified + '</div>';
                        }
                        if (res.Samples && res.Samples.length > 0) {
                            out += '<div style="margin-top: 0.8em; font-weight: bold;">Sample Matches:</div>';
                            res.Samples.forEach(function (s) {
                                out += '<div style="margin-top: 0.4em; padding: 0.4em; background: rgba(255,255,255,0.05); border-radius: 4px;">';
                                out += '<div><strong>' + escapeHtml(s.Item || '') + '</strong> (' + escapeHtml(s.Path || '') + ')</div>';
                                out += '<div style="color: #e57373;">- ' + escapeHtml(s.OldUrl || '') + '</div>';
                                out += '<div style="color: #81c784;">+ ' + escapeHtml(s.NewUrl || '') + '</div>';
                                out += '</div>';
                            });
                        }
                        rewriteResults.innerHTML = out;
                    }
                }).catch(function (err) {
                    btnRunRewrite.disabled = false;
                    if (rewriteResults) {
                        rewriteResults.innerHTML = '<div style="color: #e53935;">Error executing rewrite: ' + escapeHtml(extractErrorMessage(err)) + '</div>';
                    }
                });
            });
        });
    }

    // 5. Manual Pre-probe & Cache Controls
    if (btnStart) {
        btnStart.addEventListener('click', function () {
            apiPost('QuickStrm/Probe/Start?mode=' + (selectMode ? selectMode.value : 'Incremental')).then(function () {
                loadStatus();
            });
        });
    }

    if (btnStop) {
        btnStop.addEventListener('click', function () {
            apiPost('QuickStrm/Probe/Stop').then(function () {
                loadStatus();
            });
        });
    }

    if (btnResetFailureCache) {
        btnResetFailureCache.addEventListener('click', function () {
            apiPost('QuickStrm/Queue/Reset').then(function () {
                loadQueue();
                loadSummary();
                if (typeof Dashboard !== 'undefined' && Dashboard.alert) {
                    Dashboard.alert('Failure cache has been reset.');
                }
            });
        });
    }

    // ── Page Lifecycle ──

    view.addEventListener('viewshow', function () {
        loadSummary();
        loadStatus();
        loadQueue();
        loadConfig();
    });

    view.addEventListener('viewhide', function () {
        stopPolling();
        stopQueuePolling();
        stopCountdown();
    });
}
