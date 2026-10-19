export default function (view) {
    'use strict';

    var pluginId = '1278dbd6-8399-46c0-8e64-31f2d9e022ae';

    // ── Metric Card DOM references ──
    var metricCircuitState = view.querySelector('#metricCircuitState');
    var metricCircuitSub = view.querySelector('#metricCircuitSub');
    var metricCooldownTimer = view.querySelector('#metricCooldownTimer');
    var metricCooldownSub = view.querySelector('#metricCooldownSub');
    var metricTotalProbes = view.querySelector('#metricTotalProbes');
    var metricTotalProbesSub = view.querySelector('#metricTotalProbesSub');
    var metricCacheSize = view.querySelector('#metricCacheSize');
    var metricCacheSub = view.querySelector('#metricCacheSub');

    // ── Pre-Probe & Details DOM references ──
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
    var chkAutoProbe = view.querySelector('#chkEnableAutoProbe');
    var chkAutoDelete = view.querySelector('#chkEnableAutoDelete');
    var txtMarker = view.querySelector('#txtMountMarkerFileName');
    var circuitBanner = view.querySelector('#circuitBreakerBanner');
    var circuitStatus = view.querySelector('#circuitBreakerStatus');
    var form = view.querySelector('.quickStrmConfigurationForm');

    var pollTimer = null;
    var queuePollTimer = null;
    var countdownTimer = null;
    var cooldownEndsAt = 0;
    var circuitState = 'Closed';

    // Renders the real-time circuit state and cooldown timer across banner & metric cards
    function renderCircuit() {
        var left = Math.max(0, Math.ceil((cooldownEndsAt - Date.now()) / 1000));
        var m = Math.floor(left / 60);
        var s = left % 60;
        var timeStr = m + ':' + (s < 10 ? '0' : '') + s;

        if (circuitState === 'Closed') {
            if (circuitBanner) {
                circuitBanner.style.display = 'none';
            }
            if (metricCircuitState) {
                metricCircuitState.textContent = 'Closed';
                metricCircuitState.style.color = '#4caf50';
            }
            if (metricCircuitSub) {
                metricCircuitSub.textContent = 'Normal Operation';
            }
            if (metricCooldownTimer) {
                metricCooldownTimer.textContent = '0:00';
                metricCooldownTimer.style.color = 'var(--text-muted-color, #888)';
            }
            if (metricCooldownSub) {
                metricCooldownSub.textContent = 'Inactive';
            }
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

    // ── Helpers ──

    function apiGet(path) {
        return ApiClient.ajax({ url: ApiClient.getUrl(path), type: 'GET', dataType: 'json' });
    }

    function apiPost(path) {
        return ApiClient.ajax({ url: ApiClient.getUrl(path), type: 'POST' });
    }

    // ── Data loading ──

    function loadSummary() {
        apiGet('QuickStrm/Summary').then(function (data) {
            if (lblTotal) { lblTotal.textContent = data.Total; }
            if (lblProbed) { lblProbed.textContent = data.Probed; }
            if (lblUnprobed) { lblUnprobed.textContent = data.Unprobed; }

            if (metricTotalProbes) {
                metricTotalProbes.textContent = data.Probed;
            }
            if (metricTotalProbesSub) {
                metricTotalProbesSub.textContent = data.Probed + ' probed / ' + data.Total + ' total';
            }
            if (typeof data.CacheSize !== 'undefined' && metricCacheSize) {
                metricCacheSize.textContent = data.CacheSize;
                if (metricCacheSub) {
                    metricCacheSub.textContent = data.CacheSize === 1 ? '1 failed item' : data.CacheSize + ' failed items';
                }
            }
        });
    }

    function loadQueue() {
        apiGet('QuickStrm/Queue').then(function (data) {
            if (lblQueue) { lblQueue.textContent = data.Waiting + data.Running; }
            if (lblQueueDetail) { lblQueueDetail.textContent = data.Waiting + ' waiting / ' + data.Running + ' running'; }

            circuitState = data.CircuitState || (data.CircuitTripped ? 'Open' : 'Closed');
            cooldownEndsAt = Date.now() + (data.CooldownSecondsRemaining || 0) * 1000;

            if (typeof data.CacheSize !== 'undefined' && metricCacheSize) {
                metricCacheSize.textContent = data.CacheSize;
                if (metricCacheSub) {
                    metricCacheSub.textContent = data.CacheSize === 1 ? '1 failed item' : data.CacheSize + ' failed items';
                }
            }

            renderCircuit();
            startCountdown();

            // Background persistence starts independently of the probe task; keep a light 3s poll running
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

    function loadConfig() {
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            if (chkAutoProbe) { chkAutoProbe.checked = config.EnableAutoProbe; }
            if (chkAutoDelete) { chkAutoDelete.checked = !!config.EnableAutoDelete; }
            if (txtMarker) { txtMarker.value = config.MountMarkerFileName || ''; }
        });
    }

    // ── Polling ──

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

    // ── Event handlers ──

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

    if (form) {
        form.addEventListener('submit', function (e) {
            e.preventDefault();
            ApiClient.getPluginConfiguration(pluginId).then(function (config) {
                config.EnableAutoProbe = chkAutoProbe ? chkAutoProbe.checked : false;
                config.EnableAutoDelete = chkAutoDelete ? chkAutoDelete.checked : false;
                config.MountMarkerFileName = txtMarker ? txtMarker.value.trim() : '';
                ApiClient.updatePluginConfiguration(pluginId, config).then(function () {
                    Dashboard.processPluginConfigurationUpdateResult();
                });
            });
        });
    }

    // ── Page lifecycle ──

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
