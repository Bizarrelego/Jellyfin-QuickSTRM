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
        view._qsState = view._qsState || {
            currentConfig: null,
            queuePollInterval: null,
            isPolling: false,
            idlePollCount: 0,
            editingInstanceIndex: -1,
            probeTotalCount: 0
        };
        var qsState = view._qsState;
        var ctx = {};
        view._quickstrmContext = ctx;

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
        var chkAutoProbe = view.querySelector('#chkEnableSweeper') || view.querySelector('#chkEnableAutoProbe');
        var chkAutoDelete = view.querySelector('#chkEnableAutoHeal') || view.querySelector('#chkEnableAutoDelete');
        var chkEnableAutoHeal = view.querySelector('#chkEnableAutoHeal') || chkAutoDelete;
        var chkEnableSweeper = view.querySelector('#chkEnableSweeper') || chkAutoProbe;
        var chkNextEp = view.querySelector('#chkEnableNextEpisodePreWarm');
        var chkChapters = view.querySelector('#chkSynthesizeVirtualChapters');
        var txtMarker = view.querySelector('#txtMountMarkerFileName');
        var tabButtons = view.querySelectorAll('.quickstrm-tab-btn');
        var tabContents = view.querySelectorAll('.quickstrm-tab-content');

        // ── 3. Native Auto-Heal Feed DOM references ──
        var txtRadarrFeedUrl = view.querySelector('#txtRadarrFeedUrl');
        var txtSonarrFeedUrl = view.querySelector('#txtSonarrFeedUrl');
        var btnCopyRadarrUrl = view.querySelector('#btnCopyRadarrUrl');
        var btnCopySonarrUrl = view.querySelector('#btnCopySonarrUrl');
        var btnRefreshAutoHealFeeds = view.querySelector('#btnRefreshAutoHealFeeds');
        var lblAutoHealRadarrCount = view.querySelector('#lblAutoHealRadarrCount');
        var lblAutoHealSonarrCount = view.querySelector('#lblAutoHealSonarrCount');

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
            loadAutoHealStatus();
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
        if (countdownTimer) {
            clearInterval(countdownTimer);
        }
        countdownTimer = setInterval(renderCircuit, 1000);
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

    function pollQueueStatus(viewEl) {
        var v = viewEl || view;
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

            var queueCount = typeof data.QueueCount === 'number' ? data.QueueCount : waiting;
            var activeCount = typeof data.ActiveCount === 'number' ? data.ActiveCount : running;
            var isScanning = data.ScanningLibrary === true;
            var isRunningFlag = typeof data.IsRunning === 'boolean' ? data.IsRunning : (running > 0 || waiting > 0 || isScanning);

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

            // Evaluate idle condition: 2 consecutive idle polls required before hiding progress container
            var isIdle = !isRunningFlag && !isScanning && queueCount === 0 && activeCount === 0;
            if (isIdle) {
                v._qsState.idlePollCount = (v._qsState.idlePollCount || 0) + 1;
            } else {
                v._qsState.idlePollCount = 0;
            }

            var progressContainer = v.querySelector('#probeProgressContainer') || v.querySelector('#probeProgressSection');
            if (progressContainer) {
                if (v._qsState.idlePollCount >= 2) {
                    progressContainer.style.display = 'none';
                } else {
                    progressContainer.style.display = 'block';
                }
            }

            // If background scan is actively enumerating library items
            if (isScanning && low === 0 && running === 0) {
                if (lblStatus) {
                    lblStatus.textContent = 'Status: Scanning Library...';
                    lblStatus.style.color = '#4fc3f7';
                }
                if (progressBar) {
                    progressBar.classList.add('indeterminate');
                }
                if (btnStart) {
                    btnStart.style.display = 'none';
                }
                if (btnStop) {
                    btnStop.style.display = 'inline-flex';
                }
                return;
            }

            if (v._qsState.idlePollCount >= 2) {
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

                v._qsState.probeTotalCount = 0;
                restoreProbeButton();

                if (btnStart) {
                    btnStart.style.display = 'inline-flex';
                }
                if (btnStop) {
                    btnStop.style.display = 'none';
                }

                if (v._qsState.queuePollInterval) {
                    clearInterval(v._qsState.queuePollInterval);
                    v._qsState.queuePollInterval = null;
                }
                v._qsState.isPolling = false;

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
            if (v._qsState.probeTotalCount > 0) {
                if (progressBar) {
                    progressBar.classList.remove('indeterminate');
                    var remaining = low + running;
                    var processed = Math.max(0, v._qsState.probeTotalCount - remaining);
                    var pct = Math.min(100, Math.round((processed / v._qsState.probeTotalCount) * 100));
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

    // ── Native Auto-Heal Feed Functions ──

    function getFeedBaseUrl() {
        var base = window.location.origin;
        if (window.ApiClient && typeof ApiClient.serverAddress === 'function' && ApiClient.serverAddress()) {
            base = ApiClient.serverAddress().replace(/\/+$/, '');
        }
        return base;
    }

    function loadAutoHealStatus() {
        var base = getFeedBaseUrl();
        var radarrUrl = base + '/QuickStrm/AutoHeal/Radarr';
        var sonarrUrl = base + '/QuickStrm/AutoHeal/Sonarr';

        if (txtRadarrFeedUrl) { txtRadarrFeedUrl.value = radarrUrl; }
        if (txtSonarrFeedUrl) { txtSonarrFeedUrl.value = sonarrUrl; }

        return apiGet('QuickStrm/AutoHeal/Summary')
            .then(function (res) {
                if (lblAutoHealRadarrCount) {
                    lblAutoHealRadarrCount.textContent = (res && res.RadarrCount != null) ? res.RadarrCount : '0';
                }
                if (lblAutoHealSonarrCount) {
                    lblAutoHealSonarrCount.textContent = (res && res.SonarrCount != null) ? res.SonarrCount : '0';
                }
            })
            .catch(function () {
                // Keep existing display if endpoint temporarily unavailable
            });
    }

    function showCopyFeedback(btn) {
        var span = btn.querySelector('span:last-child') || btn;
        var orig = span.textContent;
        span.textContent = 'Copied!';
        btn.style.background = '#4caf50';
        btn.style.color = '#fff';
        setTimeout(function () {
            span.textContent = orig;
            btn.style.background = '';
            btn.style.color = '';
        }, 2000);
    }

    function wireFeedCopyButton(btn, inputEl) {
        if (!btn || !inputEl) { return; }
        btn.addEventListener('click', function (e) {
            e.preventDefault();
            var text = inputEl.value;
            if (!text) { return; }

            if (navigator.clipboard && navigator.clipboard.writeText) {
                navigator.clipboard.writeText(text)
                    .then(function () { showCopyFeedback(btn); })
                    .catch(function () {
                        inputEl.select();
                        document.execCommand('copy');
                        showCopyFeedback(btn);
                    });
            } else {
                inputEl.select();
                document.execCommand('copy');
                showCopyFeedback(btn);
            }
        });
    }

    wireFeedCopyButton(btnCopyRadarrUrl, txtRadarrFeedUrl);
    wireFeedCopyButton(btnCopySonarrUrl, txtSonarrFeedUrl);

    if (btnRefreshAutoHealFeeds) {
        btnRefreshAutoHealFeeds.addEventListener('click', function (e) {
            e.preventDefault();
            loadAutoHealStatus();
        });
    }

    function saveFullConfig(viewEl, silent) {
        var v = viewEl || view;
        return ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            var chkSweeper = v.querySelector('#chkEnableSweeper') || v.querySelector('#chkEnableAutoProbe');
            var chkAutoHeal = v.querySelector('#chkEnableAutoHeal') || v.querySelector('#chkEnableAutoDelete');
            var chkNextEp = v.querySelector('#chkEnableNextEpisodePreWarm');
            var chkChapters = v.querySelector('#chkSynthesizeVirtualChapters');
            var txtMarker = v.querySelector('#txtMountMarkerFileName');

            if (chkSweeper) {
                config.EnableAutoProbe = chkSweeper.checked;
            }
            if (chkAutoHeal) {
                config.EnableAutoDelete = chkAutoHeal.checked;
            }
            if (chkNextEp) {
                config.EnableNextEpisodePreWarm = chkNextEp.checked;
            }
            if (chkChapters) {
                config.SynthesizeVirtualChapters = chkChapters.checked;
            }
            if (txtMarker) {
                config.MountMarkerFileName = txtMarker.value.trim();
            }

            if (v._qsState) {
                v._qsState.currentConfig = config;
            }

            return ApiClient.updatePluginConfiguration(pluginId, config).then(function (res) {
                if (!silent && window.Dashboard && typeof Dashboard.processPluginConfigurationUpdateResult === 'function') {
                    Dashboard.processPluginConfigurationUpdateResult();
                }
                return res;
            });
        });
    }

    function loadConfig() {
        return ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            if (view._qsState) {
                view._qsState.currentConfig = config;
            }

            var chkSweeper = view.querySelector('#chkEnableSweeper') || view.querySelector('#chkEnableAutoProbe');
            var chkAutoHeal = view.querySelector('#chkEnableAutoHeal') || view.querySelector('#chkEnableAutoDelete');
            var chkNextEp = view.querySelector('#chkEnableNextEpisodePreWarm');
            var chkChapters = view.querySelector('#chkSynthesizeVirtualChapters');
            var txtMarker = view.querySelector('#txtMountMarkerFileName');

            if (chkSweeper) { chkSweeper.checked = !!config.EnableAutoProbe; }
            if (chkAutoHeal) { chkAutoHeal.checked = !!config.EnableAutoDelete; }
            if (chkNextEp) { chkNextEp.checked = config.EnableNextEpisodePreWarm !== false; }
            if (chkChapters) { chkChapters.checked = config.SynthesizeVirtualChapters !== false; }
            if (txtMarker) { txtMarker.value = config.MountMarkerFileName || ''; }

            return config;
        });
    }

    // ── Polling Management ──

    function startPolling() {
        if (view._qsState.queuePollInterval) {
            clearInterval(view._qsState.queuePollInterval);
            view._qsState.queuePollInterval = null;
        }
        view._qsState.isPolling = true;
        view._qsState.idlePollCount = 0;
        var progressContainer = view.querySelector('#probeProgressContainer') || view.querySelector('#probeProgressSection');
        if (progressContainer) {
            progressContainer.style.display = 'block';
        }
        pollQueueStatus(view);
        view._qsState.queuePollInterval = setInterval(function () {
            pollQueueStatus(view);
        }, 1500);
    }

    function stopPolling() {
        if (view._qsState.queuePollInterval) {
            clearInterval(view._qsState.queuePollInterval);
            view._qsState.queuePollInterval = null;
        }
        view._qsState.isPolling = false;
    }

    function startQueuePolling() {
        startPolling();
    }

    function stopQueuePolling() {
        stopPolling();
    }

    // ── Event Handlers ──

    // 1. Settings Form Submit & Auto-Save
    if (form) {
        form.addEventListener('submit', function (e) {
            e.preventDefault();
            saveFullConfig(view, false);
        });
    }

    var btnSaveMaintenance = view.querySelector('#btnSaveMaintenanceSettings');
    if (btnSaveMaintenance) {
        btnSaveMaintenance.addEventListener('click', function (e) {
            e.preventDefault();
            saveFullConfig(view, false);
        });
    }

    // Auto-Save Maintenance Settings on Change
    var chkAutoHealEl = view.querySelector('#chkEnableAutoHeal') || view.querySelector('#chkEnableAutoDelete');
    if (chkAutoHealEl) {
        chkAutoHealEl.addEventListener('change', function () {
            saveFullConfig(view, true);
        });
    }
    var chkSweeperEl = view.querySelector('#chkEnableSweeper') || view.querySelector('#chkEnableAutoProbe');
    if (chkSweeperEl) {
        chkSweeperEl.addEventListener('change', function () {
            saveFullConfig(view, true);
        });
    }
    var chkNextEpEl = view.querySelector('#chkEnableNextEpisodePreWarm');
    if (chkNextEpEl) {
        chkNextEpEl.addEventListener('change', function () {
            saveFullConfig(view, true);
        });
    }
    var chkChaptersEl = view.querySelector('#chkSynthesizeVirtualChapters');
    if (chkChaptersEl) {
        chkChaptersEl.addEventListener('change', function () {
            saveFullConfig(view, true);
        });
    }

    // 2. STRM Bulk URL Rewriter
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
                    view._qsState.probeTotalCount = enqueued;

                    // Update button text to "Probing in Progress..." while running
                    btnStart.innerHTML = '<span class="quickstrm-btn-spinner"></span> Probing in Progress...';

                    // Show non-blocking toast
                    var msg = (result && (result.Message || result.message)) || ('Queued ' + enqueued + ' items for probing.');
                    if (typeof Dashboard !== 'undefined' && typeof Dashboard.alert === 'function') {
                        Dashboard.alert({ message: msg });
                    }

                    startPolling();
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
                stopPolling();
                restoreProbeButton();
                pollQueueStatus(view);
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

    // Auto-format URLs on blur
    [txtRewriteOldPrefix, txtRewriteNewPrefix].forEach(function (input) {
        if (!input) { return; }
        input.addEventListener('blur', function () {
            autoFormatUrlInput(input);
        });
    });

    // ── Page Lifecycle ──

    ctx.loadPageData = function () {
        initTabs();
        loadSummary();
        loadStatus();
        loadQueue();
        loadConfig();
        loadAutoHealStatus();
        loadLedger();
    };

    view.addEventListener('viewshow', function () {
        ctx.loadPageData();
    });

    view.addEventListener('pageshow', function () {
        ctx.loadPageData();
    });

    view.addEventListener('viewhide', function () {
        stopPolling();
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
