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
        var statCircuitState = view.querySelector('#statCircuitState') || metricCircuitState;
        var btnResetBreaker = view.querySelector('#btnResetBreaker');
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

        var badgeSavedSweeper = view.querySelector('#badgeSavedSweeper');
        var badgeSavedAutoHeal = view.querySelector('#badgeSavedAutoHeal');
        var badgeSavedNextEp = view.querySelector('#badgeSavedNextEp');
        var badgeSavedChapters = view.querySelector('#badgeSavedChapters');

        // ── 3. Native Auto-Heal Feed DOM references ──
        var txtRadarrFeedUrl = view.querySelector('#txtRadarrFeedUrl');
        var txtSonarrFeedUrl = view.querySelector('#txtSonarrFeedUrl');
        var btnCopyRadarrUrl = view.querySelector('#btnCopyRadarrUrl');
        var btnCopySonarrUrl = view.querySelector('#btnCopySonarrUrl');
        var btnRefreshAutoHealFeeds = view.querySelector('#btnRefreshAutoHealFeeds');
        var lblAutoHealRadarrCount = view.querySelector('#lblAutoHealRadarrCount');
        var lblAutoHealSonarrCount = view.querySelector('#lblAutoHealSonarrCount');
        var lblRecoveryQueueCount = view.querySelector('#lblRecoveryQueueCount');
        var tblAutoHealItems = view.querySelector('#tblAutoHealItems');
        var tblAutoHealBody = view.querySelector('#tblAutoHealBody');
        var emptyAutoHealMessage = view.querySelector('#emptyAutoHealMessage');
        var tableAutoHealContainer = view.querySelector('#tableAutoHealContainer');

        // ── 4. STRM Tools DOM references ──
        var txtRewriteOldPrefix = view.querySelector('#txtRewriteOldPrefix');
        var txtRewriteNewPrefix = view.querySelector('#txtRewriteNewPrefix');
        var chkRewriteDryRun = view.querySelector('#chkRewriteDryRun');
        var btnRunRewrite = view.querySelector('#btnRewriteUrls') || view.querySelector('#btnRunRewrite');
        var rewriteResults = view.querySelector('#rewriteResults');

        // Pre-probe, Library Health & Failure cache DOM references
        var statTotalStrm = view.querySelector('#statTotalStrm');
        var statUnprobedStrm = view.querySelector('#statUnprobedStrm');
        var statProbedCount = view.querySelector('#statProbedCount');
        var statPrewarmedCount = view.querySelector('#statPrewarmedCount');
        var lblTotal = view.querySelector('#lblSummaryTotal') || statTotalStrm;
        var lblProbed = view.querySelector('#lblSummaryProbed') || statProbedCount;
        var lblUnprobed = view.querySelector('#lblSummaryUnprobed') || statUnprobedStrm;
        var lblQueue = view.querySelector('#lblSummaryQueue');
        var lblQueueDetail = view.querySelector('#lblSummaryQueueDetail');
        var selectMode = view.querySelector('#selectProbeMode') || view.querySelector('#selProbeMode');
        var btnStart = view.querySelector('#btnStartProbe');
        var btnCancelScan = view.querySelector('#btnCancelScan');
        var btnStop = view.querySelector('#btnStopProbe') || btnCancelScan;
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

        // ── Data Loading & Unified Dashboard ──

        function renderLedgerEntries(entries) {
            if (!recentActionsList) { return; }
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
        }

        function loadLedger() {
            if (!recentActionsList) { return; }
            apiGet('QuickStrm/Ledger').then(function (entries) {
                if (!Array.isArray(entries) && entries && Array.isArray(entries.Entries)) {
                    entries = entries.Entries;
                }
                renderLedgerEntries(entries);
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

        function renderAutoHealTable(items) {
            if (!tblAutoHealBody || !emptyAutoHealMessage || !tableAutoHealContainer) {
                return;
            }

            if (!Array.isArray(items) || items.length === 0) {
                tableAutoHealContainer.style.display = 'none';
                emptyAutoHealMessage.style.display = 'block';
                tblAutoHealBody.innerHTML = '';
                return;
            }

            emptyAutoHealMessage.style.display = 'none';
            tableAutoHealContainer.style.display = 'block';

            var html = '';
            for (var i = 0; i < items.length; i++) {
                var it = items[i];
                var title = escapeHtml(it.Title || it.title || 'Unknown Media');
                var id = escapeHtml(it.Id || it.id || '-');
                var rawType = (it.MediaType != null) ? it.MediaType : it.mediaType;
                var isMovie = rawType === 0 || rawType === 'Movie' || rawType === 'movie';
                var typeBadge = isMovie
                    ? '<span class="quickstrm-badge quickstrm-badge-radarr">Movie</span>'
                    : '<span class="quickstrm-badge quickstrm-badge-sonarr">Series</span>';
                var dateStr = '-';
                var rawDate = it.DateAdded || it.dateAdded;
                if (rawDate) {
                    try {
                        dateStr = new Date(rawDate).toLocaleString();
                    } catch (e) {
                        dateStr = String(rawDate);
                    }
                }

                html += '<tr style="border-bottom: 1px solid var(--line-color, rgba(255,255,255,0.06));">';
                html += '<td style="padding: 0.6em 0.8em; font-weight: 500; color: var(--text-color, #fff);">' + title + '</td>';
                html += '<td style="padding: 0.6em 0.8em;">' + typeBadge + '</td>';
                html += '<td style="padding: 0.6em 0.8em; font-family: monospace; color: var(--text-muted-color, #bbb);">' + id + '</td>';
                html += '<td style="padding: 0.6em 0.8em; font-size: 0.85em; color: var(--text-muted-color, #888);">' + escapeHtml(dateStr) + '</td>';
                html += '</tr>';
            }

            tblAutoHealBody.innerHTML = html;
        }

        function scheduleNextPoll(viewEl, delayMs) {
            var v = viewEl || view;
            if (!v || !v._qsState || v._qsState.isPolling === false) {
                return;
            }
            if (v._qsState.queuePollInterval) {
                clearTimeout(v._qsState.queuePollInterval);
                v._qsState.queuePollInterval = null;
            }
            v._qsState.queuePollInterval = setTimeout(function () {
                fetchDashboard(v);
            }, delayMs || 5000);
        }

        function fetchDashboard(viewEl) {
            var v = viewEl || view;
            var dashUrl = (typeof ApiClient.getUrl === 'function') ? ApiClient.getUrl('QuickStrm/Dashboard') : 'QuickStrm/Dashboard';
            var fetchPromise = (typeof ApiClient.getJSON === 'function')
                ? ApiClient.getJSON(dashUrl)
                : apiGet('QuickStrm/Dashboard');

            return fetchPromise.then(function (data) {
                if (!data) { return null; }

                var q = data.Queue || data.queue || {};
                var t = data.Telemetry || data.telemetry || {};
                var ah = data.AutoHeal || data.autoHeal || {};

                var isRunning = q.IsRunning || q.isRunning || false;
                var isScanning = q.ScanningLibrary || q.scanningLibrary || false;
                var queueCount = typeof q.QueueCount === 'number' ? q.QueueCount : (typeof q.queueCount === 'number' ? q.queueCount : 0);
                var activeCount = typeof q.ActiveCount === 'number' ? q.ActiveCount : (typeof q.activeCount === 'number' ? q.activeCount : 0);
                var isTripped = q.IsCircuitTripped === true || q.isCircuitTripped === true;
                var consecutiveFailures = typeof q.ConsecutiveFailures === 'number' ? q.ConsecutiveFailures : (typeof q.consecutiveFailures === 'number' ? q.consecutiveFailures : 0);
                var curCircuitState = q.CircuitState || q.circuitState || (isTripped ? 'Open' : 'Closed');

                circuitState = curCircuitState;

                // 1. Hook Circuit Breaker & Health Stats
                if (statCircuitState) {
                    statCircuitState.textContent = curCircuitState;
                    statCircuitState.style.color = curCircuitState === 'Closed' ? '#4caf50' : (curCircuitState === 'Open' ? '#e53935' : '#ff9800');
                }
                if (metricCircuitState && metricCircuitState !== statCircuitState) {
                    metricCircuitState.textContent = curCircuitState;
                    metricCircuitState.style.color = curCircuitState === 'Closed' ? '#4caf50' : (curCircuitState === 'Open' ? '#e53935' : '#ff9800');
                }
                if (metricCircuitSub) {
                    metricCircuitSub.textContent = curCircuitState === 'Closed' ? 'Normal Operation' : (curCircuitState === 'Open' ? 'Mount Offline / Cooldown' : 'Trial In Flight');
                }

                if (circuitBanner) {
                    circuitBanner.style.display = isTripped ? 'block' : 'none';
                }

                // Compact Reset Breaker button visibility
                if (btnResetBreaker) {
                    btnResetBreaker.style.display = (isTripped || consecutiveFailures > 0) ? 'inline-flex' : 'none';
                }

                // Library Health Metrics
                var totalStrm = typeof t.TotalStrmCount === 'number' ? t.TotalStrmCount : (typeof t.totalStrmCount === 'number' ? t.totalStrmCount : 0);
                var unprobedStrm = typeof t.UnprobedStrmCount === 'number' ? t.UnprobedStrmCount : (typeof t.unprobedStrmCount === 'number' ? t.unprobedStrmCount : 0);
                var probedCount = typeof t.ProbedCount === 'number' ? t.ProbedCount : (typeof t.probedCount === 'number' ? t.probedCount : 0);
                var prewarmedCount = typeof t.PrewarmedCount === 'number' ? t.PrewarmedCount : (typeof t.prewarmedCount === 'number' ? t.prewarmedCount : 0);

                if (statTotalStrm) { statTotalStrm.textContent = totalStrm; }
                if (lblTotal && lblTotal !== statTotalStrm) { lblTotal.textContent = totalStrm; }

                if (statUnprobedStrm) { statUnprobedStrm.textContent = unprobedStrm; }
                if (lblUnprobed && lblUnprobed !== statUnprobedStrm) { lblUnprobed.textContent = unprobedStrm; }

                if (statProbedCount) { statProbedCount.textContent = probedCount; }
                if (lblProbed && lblProbed !== statProbedCount) { lblProbed.textContent = probedCount; }

                if (statPrewarmedCount) { statPrewarmedCount.textContent = prewarmedCount; }

                // Persistence Queue Summary
                if (lblQueue) { lblQueue.textContent = queueCount + activeCount; }
                if (lblQueueDetail) { lblQueueDetail.textContent = queueCount + ' waiting / ' + activeCount + ' running'; }
                if (metricQueueDepth) { metricQueueDepth.textContent = queueCount; }
                if (metricActiveProbes) { metricActiveProbes.textContent = activeCount + ' / 4'; }

                // Ledger entries if present
                var ledgerEntries = t.Ledger || t.ledger;
                if (Array.isArray(ledgerEntries) && recentActionsList) {
                    renderLedgerEntries(ledgerEntries);
                }

                // Pre-Probe Progress and Action Buttons
                var isProbeActive = isRunning || isScanning;
                if (isProbeActive) {
                    if (btnStart) { btnStart.style.display = 'none'; }
                    if (btnCancelScan) { btnCancelScan.style.display = 'inline-flex'; }
                    if (btnStop && btnStop !== btnCancelScan) { btnStop.style.display = 'inline-flex'; }

                    var progContainer = v.querySelector('#probeProgressContainer') || v.querySelector('.probeProgressSection');
                    if (progContainer) { progContainer.style.display = 'block'; }

                    if (isScanning && activeCount === 0 && queueCount === 0) {
                        if (lblStatus) {
                            lblStatus.textContent = 'Status: Scanning Library...';
                            lblStatus.style.color = '#4fc3f7';
                        }
                        if (progressBar) { progressBar.classList.add('indeterminate'); }
                    } else {
                        if (progressBar) { progressBar.classList.remove('indeterminate'); }
                        var remaining = queueCount + activeCount;
                        if (remaining > (v._qsState.probeTotalCount || 0)) { v._qsState.probeTotalCount = remaining; }
                        if (v._qsState.probeTotalCount > 0) {
                            var processed = Math.max(0, v._qsState.probeTotalCount - remaining);
                            var pct = Math.min(100, Math.round((processed / v._qsState.probeTotalCount) * 100));
                            if (progressBar) { progressBar.style.width = pct + '%'; }
                            if (lblStatus) { lblStatus.textContent = 'Status: Running (' + pct + '%)'; lblStatus.style.color = ''; }
                        } else {
                            if (progressBar) { progressBar.classList.add('indeterminate'); }
                            if (lblStatus) { lblStatus.textContent = 'Status: Running'; lblStatus.style.color = ''; }
                        }
                    }
                    var activeProbes = q.ActiveProbes || q.activeProbes || [];
                    if (probeItemText) {
                        probeItemText.style.whiteSpace = 'nowrap';
                        probeItemText.style.overflow = 'hidden';
                        probeItemText.style.textOverflow = 'ellipsis';
                    }
                    if (lblCurrentItem) {
                        if (Array.isArray(activeProbes) && activeProbes.length > 0) {
                            lblCurrentItem.textContent = activeProbes.join(', ');
                        } else if (activeCount > 0) {
                            lblCurrentItem.textContent = activeCount + ' active probe(s)';
                        } else {
                            lblCurrentItem.textContent = '-';
                        }
                    } else if (probeItemText) {
                        if (Array.isArray(activeProbes) && activeProbes.length > 0) {
                            probeItemText.textContent = 'Probing: ' + activeProbes.join(', ');
                        } else if (activeCount > 0) {
                            probeItemText.textContent = 'Probing: ' + activeCount + ' active probe(s)';
                        } else {
                            probeItemText.textContent = 'Probing: -';
                        }
                    }
                } else {
                    v._qsState.probeTotalCount = 0;
                    restoreProbeButton();
                    if (btnStart) { btnStart.style.display = 'inline-flex'; }
                    if (btnCancelScan) { btnCancelScan.style.display = 'none'; }
                    if (btnStop && btnStop !== btnCancelScan) { btnStop.style.display = 'none'; }

                    if (progressBar) {
                        progressBar.classList.remove('indeterminate');
                        progressBar.style.width = '0%';
                    }
                    if (lblStatus) {
                        lblStatus.textContent = 'Status: Idle';
                        lblStatus.style.color = '';
                    }
                    if (lblCurrentItem) {
                        lblCurrentItem.textContent = '-';
                    } else if (probeItemText) {
                        probeItemText.textContent = 'Probing: -';
                    }
                }

                // Auto-Heal Summary & Recovery Table
                var radarrCount = typeof ah.RadarrCount === 'number' ? ah.RadarrCount : (typeof ah.radarrCount === 'number' ? ah.radarrCount : 0);
                var sonarrCount = typeof ah.SonarrCount === 'number' ? ah.SonarrCount : (typeof ah.sonarrCount === 'number' ? ah.sonarrCount : 0);
                var autoHealItems = ah.Items || ah.items || [];

                if (lblAutoHealRadarrCount) { lblAutoHealRadarrCount.textContent = radarrCount; }
                if (lblAutoHealSonarrCount) { lblAutoHealSonarrCount.textContent = sonarrCount; }
                if (lblRecoveryQueueCount) { lblRecoveryQueueCount.textContent = autoHealItems.length + ' items'; }

                renderAutoHealTable(autoHealItems);

                // Schedule next poll adaptively: 1500ms when active, relaxing to 5000ms when idle
                scheduleNextPoll(v, isProbeActive ? 1500 : 5000);

                return data;
            }).catch(function (err) {
                console.warn('QuickSTRM: Error fetching dashboard payload', err);
                scheduleNextPoll(v, 5000);
            });
        }

        // Compatibility Wrappers for Legacy Callers
        function pollQueueStatus(viewEl) {
            return fetchDashboard(viewEl);
        }

        function loadQueue() {
            return fetchDashboard(view);
        }

        function loadSummary() {
            return fetchDashboard(view);
        }

        function loadStatus() {
            return fetchDashboard(view);
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

            return fetchDashboard(view);
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
                clearTimeout(view._qsState.queuePollInterval);
                view._qsState.queuePollInterval = null;
            }
            view._qsState.isPolling = true;
            fetchDashboard(view);
        }

        function stopPolling() {
            if (view._qsState.queuePollInterval) {
                clearTimeout(view._qsState.queuePollInterval);
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

        function flashSavedBadge(badgeEl) {
            if (!badgeEl) { return; }
            badgeEl.classList.add('visible');
            setTimeout(function () {
                badgeEl.classList.remove('visible');
            }, 2000);
        }

        // Auto-Save Maintenance Settings on Change with inline "Saved" badge
        var chkSweeperEl = view.querySelector('#chkEnableSweeper') || view.querySelector('#chkEnableAutoProbe');
        if (chkSweeperEl) {
            chkSweeperEl.addEventListener('change', function () {
                saveFullConfig(view, true).then(function () {
                    flashSavedBadge(badgeSavedSweeper);
                });
            });
        }

        var chkAutoHealEl = view.querySelector('#chkEnableAutoHeal') || view.querySelector('#chkEnableAutoDelete');
        if (chkAutoHealEl) {
            chkAutoHealEl.addEventListener('change', function () {
                saveFullConfig(view, true).then(function () {
                    flashSavedBadge(badgeSavedAutoHeal);
                });
            });
        }

        var chkNextEpEl = view.querySelector('#chkEnableNextEpisodePreWarm');
        if (chkNextEpEl) {
            chkNextEpEl.addEventListener('change', function () {
                saveFullConfig(view, true).then(function () {
                    flashSavedBadge(badgeSavedNextEp);
                });
            });
        }

        var chkChaptersEl = view.querySelector('#chkSynthesizeVirtualChapters');
        if (chkChaptersEl) {
            chkChaptersEl.addEventListener('change', function () {
                saveFullConfig(view, true).then(function () {
                    flashSavedBadge(badgeSavedChapters);
                });
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

                // 5. POST /QuickStrm/Probe
                apiPost('QuickStrm/Probe?mode=' + encodeURIComponent(mode), JSON.stringify({ Mode: mode }))
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

        function handleCancelScan(e) {
            if (e) {
                e.preventDefault();
                e.stopPropagation();
            }
            if (btnCancelScan) {
                btnCancelScan.disabled = true;
                btnCancelScan.innerHTML = '<span class="quickstrm-btn-spinner"></span> Cancelling...';
            }
            if (btnStop && btnStop !== btnCancelScan) {
                btnStop.disabled = true;
                btnStop.innerHTML = '<span class="quickstrm-btn-spinner"></span> Stopping...';
            }
            apiPost('QuickStrm/Probe/Cancel').then(function () {
                if (btnCancelScan) {
                    btnCancelScan.disabled = false;
                    btnCancelScan.innerHTML = '<span>Cancel Scan</span>';
                }
                if (btnStop && btnStop !== btnCancelScan) {
                    btnStop.disabled = false;
                    btnStop.innerHTML = '<span>Stop</span>';
                }
                stopPolling();
                restoreProbeButton();
                fetchDashboard(view);
            }).catch(function () {
                if (btnCancelScan) {
                    btnCancelScan.disabled = false;
                    btnCancelScan.innerHTML = '<span>Cancel Scan</span>';
                }
                if (btnStop && btnStop !== btnCancelScan) {
                    btnStop.disabled = false;
                    btnStop.innerHTML = '<span>Stop</span>';
                }
                restoreProbeButton();
            });
        }

        if (btnCancelScan) {
            btnCancelScan.addEventListener('click', handleCancelScan);
        }
        if (btnStop && btnStop !== btnCancelScan) {
            btnStop.addEventListener('click', handleCancelScan);
        }

        if (btnResetFailureCache) {
            btnResetFailureCache.addEventListener('click', function () {
                apiPost('QuickStrm/Queue/Reset').then(function () {
                    fetchDashboard(view);
                    if (typeof Dashboard !== 'undefined' && Dashboard.alert) {
                        Dashboard.alert('Failure cache has been reset.');
                    }
                });
            });
        }

        if (btnResetBreaker) {
            btnResetBreaker.addEventListener('click', function (e) {
                if (e) { e.preventDefault(); e.stopPropagation(); }
                btnResetBreaker.disabled = true;
                apiPost('QuickStrm/Circuit/Reset').then(function () {
                    btnResetBreaker.disabled = false;
                    fetchDashboard(view);
                    if (typeof Dashboard !== 'undefined' && Dashboard.alert) {
                        Dashboard.alert('Circuit breaker has been reset to closed.');
                    }
                }).catch(function (err) {
                    btnResetBreaker.disabled = false;
                    showAlert('Failed to reset circuit breaker: ' + extractErrorMessage(err));
                });
            });
        }

        if (btnResetCircuit) {
            btnResetCircuit.addEventListener('click', function () {
                apiPost('QuickStrm/Circuit/Reset').then(function () {
                    fetchDashboard(view);
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
            loadConfig();
            loadAutoHealStatus();
            loadLedger();
            startPolling();
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
