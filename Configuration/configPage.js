(function () {
    'use strict';

    // Global helper for immediate tab switching
    window.QuickStrmSwitchTab = function (btnElement, tabId) {
        var page = (btnElement && btnElement.closest) ? btnElement.closest('#QuickStrmConfigPage') : document.querySelector('#QuickStrmConfigPage');
        if (!page) {
            page = document.querySelector('#QuickStrmConfigPage') || document.querySelector('#QuickStrmConfigurationPage');
        }
        if (!page) return;

        var targetTabId = tabId || (btnElement ? btnElement.getAttribute('data-tab') : 'tab-overview');
        page.querySelectorAll('.quickstrm-tab-btn').forEach(function (b) {
            if (b === btnElement || b.getAttribute('data-tab') === targetTabId) {
                b.classList.add('active');
            } else {
                b.classList.remove('active');
            }
        });

        page.querySelectorAll('.quickstrm-tab-panel, .quickstrm-tab-content').forEach(function (p) {
            if (p.id === targetTabId) {
                p.classList.add('active');
                p.style.display = 'block';
            } else {
                p.classList.remove('active');
                p.style.display = 'none';
            }
        });

        if (window.QuickStrmOnTabSwitch) {
            window.QuickStrmOnTabSwitch(targetTabId);
        }
    };

    function initQuickStrmPage(view) {
        if (!view) {
            return;
        }

        // Separate one-time event binding from data refreshing so returning to the page refreshes data
        if (view._quickstrmContext) {
            view._quickstrmContext.loadPageData();
            return;
        }

        view.dataset.quickstrmBound = 'true';
        var ctx = {};
        view._quickstrmContext = ctx;

        var editingInstanceIndex = -1;
        var queuePollInterval = null;
        var probeTotalCount = 0;

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
        var btnResetCircuit = view.querySelector('#btnResetCircuitBreaker');

        // ── 2. Settings Form DOM references ──
        var form = view.querySelector('.quickStrmConfigurationForm');
        var chkAutoProbe = view.querySelector('#chkEnableAutoProbe');
        var chkAutoDelete = view.querySelector('#chkEnableAutoDelete');
        var chkNextEp = view.querySelector('#chkEnableNextEpisodePreWarm');
        var chkChapters = view.querySelector('#chkSynthesizeVirtualChapters');
        var txtMarker = view.querySelector('#txtMountMarkerFileName');
        var txtJellyfinUrl = view.querySelector('#txtJellyfinInternalUrl') || view.querySelector('#txtJellyfinBaseUrl');
        var txtJellyfinInternalUrl = view.querySelector('#txtJellyfinInternalUrl');
        var txtJellyfinBaseUrl = view.querySelector('#txtJellyfinBaseUrl');
        var dockerWarning = view.querySelector('#dockerLocalhostWarning');
        var arrStatusBanner = view.querySelector('#arrConnectionStatusBanner');
        var tabButtons = view.querySelectorAll('.quickstrm-tab-btn');
        var tabContents = view.querySelectorAll('.quickstrm-tab-content');

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
        var btnToggleApiKey = view.querySelector('#btnToggleApiKeyVisibility');
        var addPathFrom = view.querySelector('#addArrPathFrom');
        var addPathTo = view.querySelector('#addArrPathTo');
        var addAutoHeal = view.querySelector('#addArrAutoHeal');
        var btnSaveAdd = view.querySelector('#btnSaveArrInstance') || view.querySelector('#btnSaveArrConnection');
        var btnCancelAdd = view.querySelector('#btnCancelArrInstance');
        var btnTestArr = view.querySelector('#btnTestArrConnection');

        // ── 4. STRM Tools DOM references ──
        var txtRewriteOldPrefix = view.querySelector('#txtRewriteOldPrefix');
        var txtRewriteNewPrefix = view.querySelector('#txtRewriteNewPrefix');
        var chkRewriteDryRun = view.querySelector('#chkRewriteDryRun');
        var btnRunRewrite = view.querySelector('#btnRewriteUrls') || view.querySelector('#btnRunRewrite');
        var rewriteResults = view.querySelector('#rewriteResults');

        // Pre-probe & Failure cache DOM references
        var lblTotal = view.querySelector('#lblSummaryTotal');
        var lblProbed = view.querySelector('#lblSummaryProbed');
        var lblUnprobed = view.querySelector('#lblSummaryUnprobed');
        var lblQueue = view.querySelector('#lblSummaryQueue');
        var lblQueueDetail = view.querySelector('#lblSummaryQueueDetail');
        var selectMode = view.querySelector('#selectProbeMode') || view.querySelector('#selProbeMode');
        var btnStart = view.querySelector('#btnStartProbe');
        var btnStop = view.querySelector('#btnStopProbe');
        var btnResetFailureCache = view.querySelector('#btnResetFailureCache');
    var lblStatus = view.querySelector('#probeStatusText') || view.querySelector('#lblTaskStatus');
    var progressBar = view.querySelector('#probeProgressBar') || view.querySelector('.taskProgressInner');
    var probeItemText = view.querySelector('#probeItemText');
    var lblCurrentItem = view.querySelector('#lblCurrentItem');
    var recentActionsList = view.querySelector('#recentActionsList');
    var btnRefreshLedger = view.querySelector('#btnRefreshLedger');
    var btnClearLedger = view.querySelector('#btnClearLedger');

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

    function autoFormatUrlInput(input) {
        if (!input) { return; }
        var val = (input.value || '').trim();
        if (!val) { return; }
        if (!/^https?:\/\//i.test(val)) {
            val = 'http://' + val;
        }
        val = val.replace(/\/+$/, '');
        input.value = val;
    }

    function showDrawerFeedback(isSuccess, message) {
        if (!arrStatusBanner) { return; }
        arrStatusBanner.className = 'quickstrm-inline-feedback ' + (isSuccess ? 'success' : 'error');
        var icon = isSuccess ? 'check_circle' : 'error';
        var iconColor = isSuccess ? '#81c784' : '#ef5350';
        arrStatusBanner.innerHTML = '<span class="material-icons" style="color: ' + iconColor + '; font-size: 1.2em;">' + icon + '</span>' +
            '<div style="word-break: break-word;">' + escapeHtml(message) + '</div>';
        arrStatusBanner.style.display = 'flex';
    }

    function hideDrawerFeedback() {
        if (arrStatusBanner) {
            arrStatusBanner.style.display = 'none';
        }
    }

    function checkDockerLocalhostWarning(instances) {
        if (!dockerWarning) { return; }
        var jfUrl = txtJellyfinUrl ? txtJellyfinUrl.value.trim().toLowerCase() : '';
        var isLocalhost = jfUrl.indexOf('localhost') !== -1 || jfUrl.indexOf('127.0.0.1') !== -1;
        if (!isLocalhost) {
            dockerWarning.style.display = 'none';
            return;
        }

        var hasDockerHostname = false;
        if (Array.isArray(instances)) {
            for (var i = 0; i < instances.length; i++) {
                var instUrl = instances[i] && instances[i].BaseUrl ? instances[i].BaseUrl.toLowerCase() : '';
                if (instUrl && instUrl.indexOf('localhost') === -1 && instUrl.indexOf('127.0.0.1') === -1) {
                    hasDockerHostname = true;
                    break;
                }
            }
        }

        dockerWarning.style.display = hasDockerHostname ? 'flex' : 'none';
    }

    function initTabs() {
        var activeBtn = view.querySelector('.quickstrm-tab-btn.active') || view.querySelector('.quickstrm-tab-btn');
        var targetTabId = activeBtn ? activeBtn.getAttribute('data-tab') : 'tab-overview';
        window.QuickStrmSwitchTab(activeBtn, targetTabId);
    }

    // Bulletproof Tab Switching via Event Delegation on view
    view.addEventListener('click', function (e) {
        var btn = e.target.closest('.quickstrm-tab-btn');
        if (!btn) {
            return;
        }

        e.preventDefault();
        e.stopPropagation();

        var targetTabId = btn.getAttribute('data-tab');
        window.QuickStrmSwitchTab(btn, targetTabId);
    });

    window.QuickStrmOnTabSwitch = function (targetTabId) {
        if (targetTabId === 'tab-overview') {
            loadSummary();
            loadStatus();
            loadQueue();
            loadLedger();
        } else if (targetTabId === 'tab-arr') {
            loadConfig();
        }
    };

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

    function loadLedger() {
        if (!recentActionsList) { return; }
        var ledgerUrl = (typeof ApiClient.getUrl === 'function') ? ApiClient.getUrl('QuickStrm/Ledger') : 'QuickStrm/Ledger';
        var fetchPromise = (typeof ApiClient.getJSON === 'function')
            ? ApiClient.getJSON(ledgerUrl)
            : apiGet('QuickStrm/Ledger');

        fetchPromise.then(function (entries) {
            if (!Array.isArray(entries) && entries && Array.isArray(entries.Entries)) {
                entries = entries.Entries;
            }
            if (!Array.isArray(entries) || entries.length === 0) {
                recentActionsList.innerHTML = '<div style="color: var(--text-muted-color, #777); font-style: italic;">No recent actions logged.</div>';
                return;
            }
            var html = '';
            var reversed = entries.slice().reverse();
            for (var i = 0; i < reversed.length; i++) {
                var entry = reversed[i];
                var color = 'var(--text-color, #ddd)';
                if (entry.indexOf('STRM Auto-Heal') !== -1 || entry.indexOf('Auto-Heal') !== -1) {
                    color = '#ffb74d';
                } else if (entry.indexOf('Tripped') !== -1) {
                    color = '#e57373';
                } else if (entry.indexOf('Reset') !== -1) {
                    color = '#81c784';
                }
                html += '<div style="padding: 0.35em 0; border-bottom: 1px solid rgba(255,255,255,0.06); color: ' + color + '; word-break: break-all;">' + escapeHtml(entry) + '</div>';
            }
            recentActionsList.innerHTML = html;
        }).catch(function (err) {
            recentActionsList.innerHTML = '<div style="color: #e53935;">Failed to load recent actions: ' + escapeHtml(extractErrorMessage(err)) + '</div>';
        });
    }

    function restoreProbeButton() {
        if (btnStart) {
            btnStart.disabled = false;
            btnStart.style.opacity = '';
            btnStart.style.cursor = '';
            btnStart.innerHTML = '<span>Start pre-probe</span>';
        }
        var selMode = view.querySelector('#selectProbeMode') || view.querySelector('#selProbeMode');
        if (selMode) {
            selMode.disabled = false;
        }
    }

    function pollQueueStatus() {
        var queueUrl = (typeof ApiClient.getUrl === 'function') ? ApiClient.getUrl('QuickStrm/Queue') : 'QuickStrm/Queue';
        var fetchPromise = (typeof ApiClient.getJSON === 'function')
            ? ApiClient.getJSON(queueUrl)
            : apiGet('QuickStrm/Queue');

        return fetchPromise.then(function (data) {
            if (!data) {
                return;
            }

            var low = typeof data.LowPriorityWaiting === 'number' ? data.LowPriorityWaiting : 0;
            var high = typeof data.HighPriorityWaiting === 'number' ? data.HighPriorityWaiting : 0;
            var running = typeof data.Running === 'number' ? data.Running : 0;
            var waiting = typeof data.Waiting === 'number' ? data.Waiting : (low + high);

            // Map metrics to DOM
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

            var isCircuitOpen = data.CircuitBreakerOpen === true || data.CircuitTripped === true;
            circuitState = data.CircuitState || (isCircuitOpen ? 'Open' : 'Closed');
            cooldownEndsAt = Date.now() + (data.CooldownSecondsRemaining || 0) * 1000;
            renderCircuit();

            // Evaluate if LowPriorityWaiting === 0 and Running === 0
            if (low === 0 && running === 0) {
                if (lblStatus) {
                    lblStatus.textContent = 'Status: Idle';
                    lblStatus.style.color = '';
                }
                if (lblCurrentItem) {
                    lblCurrentItem.textContent = '-';
                } else if (probeItemText) {
                    probeItemText.textContent = 'Probing: -';
                }

                if (progressBar) {
                    progressBar.classList.remove('indeterminate');
                    progressBar.style.width = '0%';
                }

                probeTotalCount = 0;
                restoreProbeButton();

                if (btnStart) {
                    btnStart.style.display = 'inline-flex';
                }
                if (btnStop) {
                    btnStop.style.display = 'none';
                }

                if (queuePollInterval) {
                    clearInterval(queuePollInterval);
                    queuePollInterval = null;
                }

                loadSummary();
                return;
            }

            // Probe is running or queued
            if (btnStart) {
                btnStart.style.display = 'none';
            }
            if (btnStop) {
                btnStop.style.display = 'inline-flex';
            }

            // If a total count was captured at the start of the probe, calculate the completion percentage.
            // Otherwise, implement an indeterminate CSS animation on .taskProgressInner while Running > 0.
            if (probeTotalCount > 0) {
                if (progressBar) {
                    progressBar.classList.remove('indeterminate');
                    var remaining = low + running;
                    var processed = Math.max(0, probeTotalCount - remaining);
                    var pct = Math.min(100, Math.round((processed / probeTotalCount) * 100));
                    progressBar.style.width = pct + '%';
                    if (lblStatus) {
                        lblStatus.textContent = 'Status: Running (' + pct + '%)';
                    }
                }
            } else if (running > 0) {
                if (progressBar) {
                    progressBar.classList.add('indeterminate');
                }
                if (lblStatus) {
                    lblStatus.textContent = 'Status: Running';
                }
            } else {
                if (progressBar) {
                    progressBar.classList.remove('indeterminate');
                    progressBar.style.width = '0%';
                }
                if (lblStatus) {
                    lblStatus.textContent = 'Status: Waiting';
                }
            }

            if (lblCurrentItem) {
                lblCurrentItem.textContent = data.CurrentItem || (running > 0 ? (running + ' active probe(s)') : '-');
            }
        }).catch(function (err) {
            console.error('Error in pollQueueStatus:', err);
        });
    }

    function loadQueue() {
        return pollQueueStatus();
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
                restoreProbeButton();
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
        checkDockerLocalhostWarning(instances);

        if (!instances || instances.length === 0) {
            arrList.innerHTML = '<div style="color: var(--text-muted-color, #777); font-size: 0.9em; padding: 1em 0;">No *arr instances configured. Click "+ Add Instance" above to connect.</div>';
            return;
        }

        var unsyncedLabel = defaultUnsyncedText || 'Not Synced';
        var html = '';

        instances.forEach(function (inst, index) {
            var isSynced = inst.ProvisionedWebhookId != null && inst.ProvisionedWebhookId > 0;
            var arrType = inst.ArrType || 'Radarr';
            var typeBadgeClass = 'quickstrm-badge-radarr';
            if (arrType.toLowerCase() === 'sonarr') {
                typeBadgeClass = 'quickstrm-badge-sonarr';
            } else if (arrType.toLowerCase() === 'whisparr') {
                typeBadgeClass = 'quickstrm-badge-whisparr';
            }

            var statusBadge;
            if (isSynced) {
                statusBadge = '<span class="quickstrm-badge quickstrm-badge-green sync-status synced">SYNCED #' + escapeHtml(inst.ProvisionedWebhookId) + '</span>';
            } else if (inst.LastSyncError && inst.LastSyncError.trim().length > 0) {
                statusBadge = '<span class="quickstrm-badge quickstrm-badge-red sync-status unsynced">SYNC FAILED</span>';
            } else {
                statusBadge = '<span class="quickstrm-badge quickstrm-badge-red sync-status unsynced">' + escapeHtml(unsyncedLabel) + '</span>';
            }

            var healBadge = inst.AutoHealOn404
                ? '<span class="quickstrm-badge quickstrm-badge-green">AUTO-HEAL</span>'
                : '<span class="quickstrm-badge" style="background: rgba(255,255,255,0.06); color: #888;">AUTO-HEAL OFF</span>';

            var hasPathFrom = !!(inst.PathFrom && inst.PathFrom.trim());
            var hasPathTo = !!(inst.PathTo && inst.PathTo.trim());
            var pathMappingLabel;
            if (!hasPathFrom && !hasPathTo) {
                pathMappingLabel = 'Direct (Shared Mount)';
            } else {
                pathMappingLabel = (inst.PathFrom || '(none)') + ' \u2192 ' + (inst.PathTo || '(none)');
            }

            var syncTimeStr = '';
            if (inst.LastSyncUtc) {
                try {
                    syncTimeStr = '<span style="font-size: 0.8em; opacity: 0.6; margin-left: 0.4em;">Synced: ' + new Date(inst.LastSyncUtc).toLocaleTimeString() + '</span>';
                } catch (e) {
                }
            }

            var errorBox = '';
            if (inst.LastSyncError && inst.LastSyncError.trim().length > 0) {
                var hint = '';
                var errLower = inst.LastSyncError.toLowerCase();
                if (errLower.indexOf('could not reach') !== -1 || errLower.indexOf('refused') !== -1 || errLower.indexOf('network') !== -1 || errLower.indexOf('localhost') !== -1) {
                    hint = '<div style="margin-top: 0.35em; font-size: 0.82em; opacity: 0.85;">Tip: Verify container network or host port accessibility.</div>';
                }
                errorBox = '<div class="quickstrm-card-error-callout">' +
                    '<div style="font-weight: 600; display: flex; align-items: center; gap: 0.4em; margin-bottom: 0.2em;">' +
                        '<span class="material-icons" style="font-size: 16px; color: #ef5350;">error_outline</span>' +
                        '<span>Sync Error:</span>' +
                    '</div>' +
                    '<div style="word-break: break-word;">' + escapeHtml(inst.LastSyncError) + '</div>' +
                    hint +
                '</div>';
            }

            html += '<div class="quickstrm-arr-card" data-index="' + index + '">' +
                '<div style="display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 0.8em;" class="quickstrm-card-header">' +
                    '<div style="flex: 1; min-width: 260px;">' +
                        '<div style="display: flex; align-items: center; flex-wrap: wrap; gap: 0.5em; margin-bottom: 0.4em;">' +
                            '<span style="font-weight: 600; font-size: 1.1em; color: var(--text-color, #fff);">' + escapeHtml(inst.Name || 'Unnamed') + '</span>' +
                            '<span class="quickstrm-badge ' + typeBadgeClass + '">' + escapeHtml(arrType) + '</span>' +
                            statusBadge +
                            healBadge +
                            syncTimeStr +
                        '</div>' +
                        '<div style="font-size: 0.84em; color: var(--text-muted-color, #888); word-break: break-all;">' +
                            '<span>' + escapeHtml(inst.BaseUrl || '') + '</span>' +
                            '<span style="margin: 0 0.5em; opacity: 0.5;">|</span>' +
                            '<span>Path: ' + escapeHtml(pathMappingLabel) + '</span>' +
                        '</div>' +
                    '</div>' +
                    '<div style="display: flex; gap: 0.5em; align-items: center;" class="quickstrm-card-actions">' +
                        '<button is="emby-button" type="button" class="btnSyncCard raised emby-button" data-index="' + index + '" style="padding: 0.4em 0.85em; font-size: 0.85em; margin: 0;">' +
                            '<span>Sync / Test</span>' +
                        '</button>' +
                        '<button is="emby-button" type="button" class="btnEditInstance raised emby-button" data-index="' + index + '" style="padding: 0.4em 0.85em; font-size: 0.85em; margin: 0;">' +
                            '<span>Edit</span>' +
                        '</button>' +
                        '<button is="emby-button" type="button" class="btnDeleteInstance btn-remove-arr raised emby-button" data-index="' + index + '" style="background: #b71c1c; color: white; padding: 0.4em 0.85em; font-size: 0.85em; margin: 0;">' +
                            '<span>Remove</span>' +
                        '</button>' +
                    '</div>' +
                '</div>' +
                errorBox +
            '</div>';
        });

        arrList.innerHTML = html;

        // Wire Sync / Test buttons
        var syncBtns = arrList.querySelectorAll('.btnSyncCard');
        syncBtns.forEach(function (btn) {
            btn.addEventListener('click', function (e) {
                e.preventDefault();
                e.stopPropagation();
                var idx = parseInt(btn.getAttribute('data-index'), 10);
                var span = btn.querySelector('span');
                var origText = span ? span.innerHTML : btn.innerHTML;
                if (span) {
                    span.innerHTML = '<span class="quickstrm-spinner"></span>Syncing...';
                }
                btn.disabled = true;

                apiPost('QuickStrm/Arr/SyncInstance/' + idx)
                    .then(function (res) {
                        btn.disabled = false;
                        if (span) { span.innerHTML = origText; }
                        loadConfig();
                        var isOk = res && (res.Success || res.success);
                        var msg = (res && (res.Message || res.message)) || (isOk ? 'Sync successful.' : 'Sync failed.');
                        if (!isOk) {
                            showAlert('Sync Failed: ' + msg);
                        }
                    })
                    .catch(function (err) {
                        btn.disabled = false;
                        if (span) { span.innerHTML = origText; }
                        showAlert('Sync Error: ' + extractErrorMessage(err));
                        loadConfig();
                    });
            });
        });

        // Wire Edit buttons
        var editBtns = arrList.querySelectorAll('.btnEditInstance');
        editBtns.forEach(function (btn) {
            btn.addEventListener('click', function (e) {
                var target = e.currentTarget || btn;
                editingInstanceIndex = target.dataset.index;
                hideDrawerFeedback();

                ApiClient.getPluginConfiguration(pluginId).then(function (config) {
                    var inst = config && config.ArrInstances ? config.ArrInstances[editingInstanceIndex] : null;
                    if (!inst) { return; }

                    if (addName) { addName.value = inst.Name || ''; }
                    if (addType) { addType.value = inst.ArrType || 'Radarr'; }
                    if (addBaseUrl) { addBaseUrl.value = inst.BaseUrl || ''; }
                    if (addApiKey) {
                        addApiKey.value = inst.ApiKey || '';
                        addApiKey.type = 'password';
                    }
                    if (btnToggleApiKey) {
                        var toggleSpan = btnToggleApiKey.querySelector('span');
                        if (toggleSpan) { toggleSpan.textContent = 'Show'; } else { btnToggleApiKey.textContent = 'Show'; }
                    }
                    if (addPathFrom) { addPathFrom.value = inst.PathFrom || ''; }
                    if (addPathTo) { addPathTo.value = inst.PathTo || ''; }
                    if (addAutoHeal) { addAutoHeal.checked = inst.AutoHealOn404 !== false; }

                    if (addPanel) {
                        addPanel.style.display = 'block';
                    }

                    var arrPanelTitle = view.querySelector('#arrPanelTitle');
                    if (arrPanelTitle) {
                        arrPanelTitle.innerHTML = '<span class="material-icons" style="color: var(--theme-primary-color, #00a4dc);">edit</span> Edit *arr Connection';
                    }

                    if (btnSaveAdd) {
                        var span = btnSaveAdd.querySelector('span');
                        if (span) {
                            span.textContent = 'Update Connection';
                        } else {
                            btnSaveAdd.textContent = 'Update Connection';
                        }
                    }

                    if (addPanel && typeof addPanel.scrollIntoView === 'function') {
                        addPanel.scrollIntoView({ behavior: 'smooth', block: 'center' });
                    }
                });
            });
        });

        // Wire Remove buttons
        var removeBtns = arrList.querySelectorAll('.btnDeleteInstance, .btn-remove-arr');
        removeBtns.forEach(function (btn) {
            btn.addEventListener('click', function (event) {
                event.preventDefault();
                event.stopPropagation();
                var idx = parseInt(btn.getAttribute('data-index'), 10);
                Dashboard.confirm("Are you sure you want to remove this connection?").then(function (result) {
                    if (result) {
                        removeArrInstance(idx);
                    }
                });
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
    }

    function clearAddForm() {
        if (addName) { addName.value = ''; }
        if (addType) { addType.value = 'Radarr'; }
        if (addBaseUrl) { addBaseUrl.value = ''; }
        if (addApiKey) {
            addApiKey.value = '';
            addApiKey.type = 'password';
        }
        if (btnToggleApiKey) {
            var toggleSpan = btnToggleApiKey.querySelector('span');
            if (toggleSpan) { toggleSpan.textContent = 'Show'; } else { btnToggleApiKey.textContent = 'Show'; }
        }
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
            if (txtJellyfinBaseUrl) { txtJellyfinBaseUrl.value = config.JellyfinBaseUrl || ''; }

            renderArrInstances(config.ArrInstances || [], defaultUnsyncedText);
            checkDockerLocalhostWarning(config.ArrInstances || []);
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
            clearAddForm();
            editingInstanceIndex = -1;
            if (btnSaveAdd) {
                var span = btnSaveAdd.querySelector('span');
                if (span) {
                    span.textContent = 'Save Connection';
                } else {
                    btnSaveAdd.textContent = 'Save Connection';
                }
            }
            var arrPanelTitle = view.querySelector('#arrPanelTitle');
            if (arrPanelTitle) {
                arrPanelTitle.innerHTML = '<span class="material-icons" style="color: var(--theme-primary-color, #00a4dc);">add_circle_outline</span> Add *arr Connection';
            }
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
            if (btnSaveAdd) {
                var span = btnSaveAdd.querySelector('span');
                if (span) {
                    span.textContent = 'Save Connection';
                } else {
                    btnSaveAdd.textContent = 'Save Connection';
                }
            }
            var arrPanelTitle = view.querySelector('#arrPanelTitle');
            if (arrPanelTitle) {
                arrPanelTitle.innerHTML = '<span class="material-icons" style="color: var(--theme-primary-color, #00a4dc);">add_circle_outline</span> Add *arr Connection';
            }
            editingInstanceIndex = -1;
        });
    }

    if (btnTestArr) {
        btnTestArr.addEventListener('click', function (e) {
            if (e) {
                e.preventDefault();
                e.stopPropagation();
            }
            hideDrawerFeedback();
            autoFormatUrlInput(addBaseUrl);
            var type = addType ? addType.value : 'Radarr';
            var baseUrl = addBaseUrl ? addBaseUrl.value.trim() : '';
            var apiKey = addApiKey ? addApiKey.value.trim() : '';

            if (!baseUrl || !apiKey) {
                showDrawerFeedback(false, 'Base URL and API Key are required.');
                return;
            }

            var btnSpan = btnTestArr.querySelector('span');
            var origBtnText = btnSpan ? btnSpan.innerHTML : btnTestArr.innerHTML;
            if (btnSpan) {
                btnSpan.innerHTML = '<span class="quickstrm-btn-spinner"></span> Testing...';
            } else {
                btnTestArr.innerHTML = '<span class="quickstrm-btn-spinner"></span> Testing...';
            }
            btnTestArr.disabled = true;

            var payload = {
                ArrType: type,
                BaseUrl: baseUrl,
                ApiKey: apiKey
            };

            apiPost('QuickStrm/Arr/TestConnection', payload)
                .then(function (result) {
                    btnTestArr.disabled = false;
                    if (btnSpan) { btnSpan.innerHTML = origBtnText; } else { btnTestArr.innerHTML = origBtnText; }

                    var isSuccess = result && (result.Success === true || result.success === true);
                    var msg = (result && (result.Message || result.message)) || (isSuccess ? 'Connection to ' + type + ' successful.' : 'Connection failed.');
                    showDrawerFeedback(isSuccess, msg);
                })
                .catch(function (err) {
                    btnTestArr.disabled = false;
                    if (btnSpan) { btnSpan.innerHTML = origBtnText; } else { btnTestArr.innerHTML = origBtnText; }
                    showDrawerFeedback(false, extractErrorMessage(err));
                });
        });
    }

    if (btnSaveAdd) {
        btnSaveAdd.addEventListener('click', function (e) {
            if (e) {
                e.preventDefault();
                e.stopPropagation();
            }
            hideDrawerFeedback();
            autoFormatUrlInput(addBaseUrl);

            var name = addName ? addName.value.trim() : '';
            var type = addType ? addType.value : 'Radarr';
            var baseUrl = addBaseUrl ? addBaseUrl.value.trim() : '';
            var apiKey = addApiKey ? addApiKey.value.trim() : '';
            var pathFrom = addPathFrom ? addPathFrom.value.trim() : '';
            var pathTo = addPathTo ? addPathTo.value.trim() : '';
            var autoHeal = addAutoHeal ? addAutoHeal.checked : true;

            if (!name || !baseUrl || !apiKey) {
                showDrawerFeedback(false, 'Please specify Instance Name, Base URL, and API Key.');
                return;
            }

            var btnSaveSpan = btnSaveAdd.querySelector('span');
            var origSaveText = btnSaveSpan ? btnSaveSpan.innerHTML : btnSaveAdd.innerHTML;
            if (btnSaveSpan) {
                btnSaveSpan.innerHTML = '<span class="quickstrm-btn-spinner"></span> Saving...';
            } else {
                btnSaveAdd.innerHTML = '<span class="quickstrm-btn-spinner"></span> Saving...';
            }
            btnSaveAdd.disabled = true;
            showLoading();

            ApiClient.getPluginConfiguration(pluginId).then(function (config) {
                config.ArrInstances = config.ArrInstances || [];

                var existingWebhookId = (editingInstanceIndex >= 0 && config.ArrInstances[editingInstanceIndex])
                    ? config.ArrInstances[editingInstanceIndex].ProvisionedWebhookId
                    : null;

                var newConfig = {
                    Name: name,
                    ArrType: type,
                    BaseUrl: baseUrl,
                    ApiKey: apiKey,
                    PathFrom: pathFrom,
                    PathTo: pathTo,
                    AutoHealOn404: autoHeal,
                    ProvisionedWebhookId: existingWebhookId
                };

                if (editingInstanceIndex >= 0) {
                    config.ArrInstances[editingInstanceIndex] = newConfig;
                } else {
                    config.ArrInstances.push(newConfig);
                }

                if (txtJellyfinUrl && txtJellyfinUrl.value.trim()) {
                    config.JellyfinBaseUrl = txtJellyfinUrl.value.trim();
                }

                return ApiClient.updatePluginConfiguration(pluginId, config).then(function () {
                    return apiPost('QuickStrm/Arr/TestAndSync');
                });
            }).then(function (syncResults) {
                btnSaveAdd.disabled = false;
                if (btnSaveSpan) { btnSaveSpan.innerHTML = origSaveText; } else { btnSaveAdd.innerHTML = origSaveText; }
                clearAddForm();
                hideDrawerFeedback();
                editingInstanceIndex = -1;

                if (addPanel) {
                    addPanel.style.display = 'none';
                }

                return ApiClient.getPluginConfiguration(pluginId).then(function (freshConfig) {
                    renderArrInstances(freshConfig.ArrInstances || []);
                    hideLoading();
                });
            }).catch(function (err) {
                btnSaveAdd.disabled = false;
                if (btnSaveSpan) { btnSaveSpan.innerHTML = origSaveText; } else { btnSaveAdd.innerHTML = origSaveText; }
                hideLoading();
                showDrawerFeedback(false, 'Failed to save connection: ' + extractErrorMessage(err));
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

                    var failures = [];
                    var items = [];
                    if (results && Array.isArray(results.Results)) {
                        items = results.Results;
                    } else if (Array.isArray(results)) {
                        items = results;
                    }

                    items.forEach(function (r) {
                        var ok = r.Success || r.success || r.Synced || r.synced;
                        if (!ok) {
                            var name = r.InstanceName || r.instanceName || r.Name || r.name || 'Instance';
                            var msg = r.LastSyncError || r.lastSyncError || r.Message || r.message || 'Sync failed';
                            failures.push(name + ': ' + msg);
                        }
                    });

                    loadConfig();

                    if (failures.length > 0) {
                        var errorMsg = 'Webhook synchronization failed for:\n' + failures.join('\n');
                        showAlert(errorMsg);
                    } else {
                        showAlert('All *arr instances successfully synchronized!');
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
        btnWipeArr.addEventListener('click', function (event) {
            event.preventDefault();
            event.stopPropagation();

            Dashboard.confirm("Are you sure?").then(async function (result) {
                if (result) {
                    btnWipeArr.disabled = true;
                    Dashboard.showLoadingMsg();
                    try {
                        await ApiClient.fetch({
                            url: ApiClient.getUrl('QuickStrm/Arr/WipeWebhooks'),
                            type: 'POST',
                            dataType: 'json',
                            contentType: 'application/json'
                        });
                    } catch (err) {
                        Dashboard.alert({ message: 'Error wiping webhooks: ' + extractErrorMessage(err) });
                    }
                    Dashboard.hideLoadingMsg();
                    btnWipeArr.disabled = false;
                    forceBadgesUnsynced();
                    await loadConfig('Unsynced');
                }
            });
        });
    }

    // 4. STRM Bulk URL Rewriter
    if (btnRunRewrite) {
        btnRunRewrite.addEventListener('click', function (event) {
            if (event) {
                event.preventDefault();
                event.stopPropagation();
            }

            var oldPrefix = txtRewriteOldPrefix ? txtRewriteOldPrefix.value.trim() : '';
            var newPrefix = txtRewriteNewPrefix ? txtRewriteNewPrefix.value.trim() : '';
            var dryRun = chkRewriteDryRun ? chkRewriteDryRun.checked : true;

            if (!oldPrefix || !newPrefix) {
                showAlert('Please specify both Old Prefix and New Prefix.');
                return;
            }

            var confirmPromise = dryRun
                ? Promise.resolve(true)
                : Dashboard.confirm('Are you sure you want to execute this rewrite? This will modify matching .strm files on disk.');

            confirmPromise.then(function (confirmed) {
                if (!confirmed) {
                    return;
                }

                var btnRewriteSpan = btnRunRewrite.querySelector('span');
                var origRewriteHtml = btnRewriteSpan ? btnRewriteSpan.innerHTML : btnRunRewrite.innerHTML;
                btnRunRewrite.disabled = true;
                if (btnRewriteSpan) {
                    btnRewriteSpan.innerHTML = '<span class="quickstrm-btn-spinner"></span> Rewriting...';
                } else {
                    btnRunRewrite.innerHTML = '<span class="quickstrm-btn-spinner"></span> Rewriting...';
                }

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
                    if (btnRewriteSpan) {
                        btnRewriteSpan.innerHTML = origRewriteHtml;
                    } else {
                        btnRunRewrite.innerHTML = origRewriteHtml;
                    }
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
                    if (btnRewriteSpan) {
                        btnRewriteSpan.innerHTML = origRewriteHtml;
                    } else {
                        btnRunRewrite.innerHTML = origRewriteHtml;
                    }
                    if (rewriteResults) {
                        rewriteResults.innerHTML = '<div style="color: #e53935;">Error executing rewrite: ' + escapeHtml(extractErrorMessage(err)) + '</div>';
                    }
                });
            });
        });
    }

    // 5. Manual Pre-probe & Cache Controls
    if (btnStart) {
        btnStart.addEventListener('click', function (e) {
            if (e) {
                e.preventDefault();
                e.stopPropagation();
            }

            var selMode = view.querySelector('#selectProbeMode') || view.querySelector('#selProbeMode');
            var mode = selMode ? selMode.value : 'Incremental';

            // 1. Disable #btnStartProbe and #selProbeMode
            btnStart.disabled = true;
            if (selMode) {
                selMode.disabled = true;
            }

            // 2. Change #btnStartProbe innerHTML to `<span class="quickstrm-btn-spinner"></span> Queueing Media...` and apply visual loading state
            btnStart.innerHTML = '<span class="quickstrm-btn-spinner"></span> Queueing Media...';
            btnStart.style.opacity = '0.7';
            btnStart.style.cursor = 'wait';

            // 3. Immediately call Dashboard.showLoadingMsg()
            if (typeof Dashboard !== 'undefined' && typeof Dashboard.showLoadingMsg === 'function') {
                Dashboard.showLoadingMsg();
            }

            // 4. Immediately update #probeStatusText to cyan and add .indeterminate to #probeProgressBar BEFORE awaiting POST
            var statusElem = view.querySelector('#probeStatusText') || view.querySelector('#lblTaskStatus');
            if (statusElem) {
                statusElem.textContent = 'Status: Scanning library & queueing items...';
                statusElem.style.color = '#4fc3f7';
            }
            var progBar = view.querySelector('#probeProgressBar') || view.querySelector('.taskProgressInner');
            if (progBar) {
                progBar.classList.add('indeterminate');
            }

            // 5. POST /QuickStrm/Probe/Start
            apiPost('QuickStrm/Probe/Start?mode=' + encodeURIComponent(mode), { Scope: mode, Mode: mode })
                .then(function (result) {
                    if (typeof Dashboard !== 'undefined' && typeof Dashboard.hideLoadingMsg === 'function') {
                        Dashboard.hideLoadingMsg();
                    }

                    var enqueued = (result && (typeof result.Enqueued === 'number' ? result.Enqueued : result.enqueued)) || 0;
                    probeTotalCount = enqueued;

                    // Update button text to "Probing in Progress..." while running
                    btnStart.innerHTML = '<span class="quickstrm-btn-spinner"></span> Probing in Progress...';

                    if (queuePollInterval) {
                        clearInterval(queuePollInterval);
                        queuePollInterval = null;
                    }

                    // Show non-blocking toast
                    var msg = (result && (result.Message || result.message)) || ('Queued ' + enqueued + ' items for probing.');
                    if (typeof Dashboard !== 'undefined' && typeof Dashboard.alert === 'function') {
                        Dashboard.alert({ message: msg });
                    }

                    pollQueueStatus();
                    queuePollInterval = setInterval(pollQueueStatus, 2000);
                })
                .catch(function (err) {
                    if (typeof Dashboard !== 'undefined' && typeof Dashboard.hideLoadingMsg === 'function') {
                        Dashboard.hideLoadingMsg();
                    }
                    restoreProbeButton();
                    if (statusElem) {
                        statusElem.textContent = 'Status: Error starting probe';
                        statusElem.style.color = '#ef5350';
                    }
                    if (progBar) {
                        progBar.classList.remove('indeterminate');
                        progBar.style.width = '0%';
                    }
                    var errMsg = 'Failed to start probe: ' + extractErrorMessage(err);
                    if (typeof Dashboard !== 'undefined' && typeof Dashboard.alert === 'function') {
                        Dashboard.alert({ message: errMsg });
                    } else {
                        alert(errMsg);
                    }
                });
        });
    }

    if (btnStop) {
        btnStop.addEventListener('click', function (e) {
            if (e) {
                e.preventDefault();
                e.stopPropagation();
            }
            btnStop.disabled = true;
            btnStop.innerHTML = '<span class="quickstrm-btn-spinner"></span> Stopping...';
            apiPost('QuickStrm/Probe/Stop').then(function () {
                btnStop.disabled = false;
                btnStop.innerHTML = '<span>Stop</span>';
                if (queuePollInterval) {
                    clearInterval(queuePollInterval);
                    queuePollInterval = null;
                }
                restoreProbeButton();
                pollQueueStatus();
                loadStatus();
            }).catch(function () {
                btnStop.disabled = false;
                btnStop.innerHTML = '<span>Stop</span>';
                restoreProbeButton();
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

    if (btnResetCircuit) {
        btnResetCircuit.addEventListener('click', function () {
            apiPost('QuickStrm/CircuitBreaker/Reset').then(function () {
                pollQueueStatus();
                loadSummary();
                if (typeof Dashboard !== 'undefined' && Dashboard.alert) {
                    Dashboard.alert('Circuit breaker has been reset to closed.');
                }
            }).catch(function (err) {
                showAlert('Failed to reset circuit breaker: ' + extractErrorMessage(err));
            });
        });
    }

    if (btnToggleApiKey && addApiKey) {
        btnToggleApiKey.addEventListener('click', function () {
            if (addApiKey.type === 'password') {
                addApiKey.type = 'text';
                var span = btnToggleApiKey.querySelector('span');
                if (span) { span.textContent = 'Hide'; } else { btnToggleApiKey.textContent = 'Hide'; }
            } else {
                addApiKey.type = 'password';
                var span = btnToggleApiKey.querySelector('span');
                if (span) { span.textContent = 'Show'; } else { btnToggleApiKey.textContent = 'Show'; }
            }
        });
    }

    if (btnRefreshLedger) {
        btnRefreshLedger.addEventListener('click', function () {
            loadLedger();
        });
    }

    if (btnClearLedger) {
        btnClearLedger.addEventListener('click', function () {
            var confirmPromise = typeof Dashboard !== 'undefined' && typeof Dashboard.confirm === 'function'
                ? Dashboard.confirm('Are you sure you want to clear the recent action ledger history?')
                : Promise.resolve(confirm('Are you sure you want to clear the recent action ledger history?'));

            confirmPromise.then(function (confirmed) {
                if (!confirmed) { return; }
                apiPost('QuickStrm/Ledger/Clear').then(function () {
                    loadLedger();
                }).catch(function (err) {
                    showAlert('Failed to clear ledger: ' + extractErrorMessage(err));
                });
            });
        });
    }

    // Auto-format URLs on blur and live docker warning checks
    [addBaseUrl, txtJellyfinUrl, txtRewriteOldPrefix, txtRewriteNewPrefix].forEach(function (input) {
        if (!input) { return; }
        input.addEventListener('blur', function () {
            autoFormatUrlInput(input);
            if (input === txtJellyfinUrl) {
                ApiClient.getPluginConfiguration(pluginId).then(function (cfg) {
                    checkDockerLocalhostWarning(cfg && cfg.ArrInstances ? cfg.ArrInstances : []);
                });
            }
        });
        if (input === txtJellyfinUrl) {
            input.addEventListener('input', function () {
                ApiClient.getPluginConfiguration(pluginId).then(function (cfg) {
                    checkDockerLocalhostWarning(cfg && cfg.ArrInstances ? cfg.ArrInstances : []);
                });
            });
        }
    });

    // ── Page Lifecycle ──

    ctx.loadPageData = function () {
        initTabs();
        loadSummary();
        loadStatus();
        loadQueue();
        loadConfig();
        loadLedger();
    };

    view.addEventListener('viewshow', function () {
        ctx.loadPageData();
    });

    view.addEventListener('pageshow', function () {
        ctx.loadPageData();
    });

    view.addEventListener('viewhide', function () {
        if (queuePollInterval) {
            clearInterval(queuePollInterval);
            queuePollInterval = null;
        }
        stopPolling();
        stopQueuePolling();
        stopCountdown();
    });

    // Initialize tabs and load initial state immediately
    ctx.loadPageData();
}

    // 1. AMD Support for Jellyfin's RequireJS loader
    if (typeof define === 'function' && define.amd) {
        define([], function () {
            return function (view, params) {
                // CRITICAL FIX: Call initQuickStrmPage immediately as the very first line!
                initQuickStrmPage(view);

                view.addEventListener('viewshow', function () {
                    initQuickStrmPage(view);
                });
                view.addEventListener('pageshow', function () {
                    initQuickStrmPage(view);
                });
            };
        });

        // Fallback: If configPage.js was loaded via the <script> tag injector or <img> rather than RequireJS,
        // RequireJS define() registers the module but won't invoke the controller automatically.
        // Run setTimeout fallback to bind and initialize #QuickStrmConfigPage if already in DOM.
        setTimeout(bootstrap, 50);
    }

    // 2. Event-driven lifecycle binding on document for Jellyfin / Emby viewshow and pageshow
    function onPageLifecycle(e) {
        var page = (e && e.target && (e.target.id === 'QuickStrmConfigPage' || e.target.id === 'QuickStrmConfigurationPage' || (e.target.classList && e.target.classList.contains('pluginConfigurationPage'))))
            ? e.target
            : (document.querySelector('#QuickStrmConfigPage') || document.querySelector('#QuickStrmConfigurationPage'));
        if (page) {
            initQuickStrmPage(page);
        }
    }

    document.addEventListener('viewshow', onPageLifecycle);
    document.addEventListener('pageshow', onPageLifecycle);

    // 3. Immediate execution if page element is already present in DOM
    function bootstrap() {
        var page = document.querySelector('#QuickStrmConfigPage') || document.querySelector('#QuickStrmConfigurationPage');
        if (page) {
            initQuickStrmPage(page);
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', bootstrap);
    } else {
        bootstrap();
    }
})();
