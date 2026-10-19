export default function (view) {
    'use strict';

    var pluginId = '1278dbd6-8399-46c0-8e64-31f2d9e022ae';

    // ── DOM references ──
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

    // Renders the auto-recovery status; re-run every second so the countdown moves between the 3s server polls
    function renderCircuit() {
        if (circuitState === 'Closed') {
            circuitBanner.style.display = 'none';
            return;
        }
        circuitBanner.style.display = 'block';
        var left = Math.max(0, Math.ceil((cooldownEndsAt - Date.now()) / 1000));
        if (circuitState === 'Open' && left > 0) {
            var m = Math.floor(left / 60);
            var s = left % 60;
            circuitStatus.textContent = 'Auto-recovery: next connectivity test in ' + m + ':' + (s < 10 ? '0' : '') + s + '.';
        } else {
            circuitStatus.textContent = 'Auto-recovery: testing connectivity now (half-open). One probe is being allowed through.';
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
            lblTotal.textContent = data.Total;
            lblProbed.textContent = data.Probed;
            lblUnprobed.textContent = data.Unprobed;
        });
    }

    function loadQueue() {
        apiGet('QuickStrm/Queue').then(function (data) {
            lblQueue.textContent = data.Waiting + data.Running;
            lblQueueDetail.textContent = data.Waiting + ' waiting / ' + data.Running + ' running';
            circuitState = data.CircuitState || (data.CircuitTripped ? 'Open' : 'Closed');
            cooldownEndsAt = Date.now() + (data.CooldownSecondsRemaining || 0) * 1000;
            renderCircuit();
            startCountdown();

            // Background persistence starts independently of the probe task (e.g. when someone presses play), so keep a light
            // 3s poll running while this page is visible; it is stopped on viewhide
            startQueuePolling();
        });
    }

    function loadStatus() {
        apiGet('QuickStrm/Probe/Status').then(function (data) {
            var stateMap = { Idle: 'Idle', Running: 'Running', Stopping: 'Stopping' };
            lblStatus.textContent = 'Status: ' + (stateMap[data.State] || data.State);
            lblCurrentItem.textContent = data.CurrentItem || '-';

            var pct = data.Total > 0 ? Math.round((data.Processed / data.Total) * 100) : 0;
            progressBar.style.width = pct + '%';

            if (data.State === 'Running' || data.State === 'Stopping') {
                btnStart.style.display = 'none';
                btnStop.style.display = 'inline-flex';
                startPolling();
            } else {
                btnStart.style.display = 'inline-flex';
                btnStop.style.display = 'none';
                stopPolling();
                loadSummary();
            }
        });
    }

    function loadConfig() {
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            chkAutoProbe.checked = config.EnableAutoProbe;
            chkAutoDelete.checked = !!config.EnableAutoDelete;
            txtMarker.value = config.MountMarkerFileName || '';
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

    btnStart.addEventListener('click', function () {
        apiPost('QuickStrm/Probe/Start?mode=' + selectMode.value).then(function () {
            loadStatus();
        });
    });

    btnStop.addEventListener('click', function () {
        apiPost('QuickStrm/Probe/Stop').then(function () {
            loadStatus();
        });
    });

    if (btnResetFailureCache) {
        btnResetFailureCache.addEventListener('click', function () {
            apiPost('QuickStrm/Queue/Reset').then(function () {
                loadQueue();
                if (typeof Dashboard !== 'undefined' && Dashboard.alert) {
                    Dashboard.alert('Failure cache has been reset.');
                }
            });
        });
    }

    form.addEventListener('submit', function (e) {
        e.preventDefault();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            config.EnableAutoProbe = chkAutoProbe.checked;
            config.EnableAutoDelete = chkAutoDelete.checked;
            config.MountMarkerFileName = txtMarker.value.trim();
            ApiClient.updatePluginConfiguration(pluginId, config).then(function () {
                Dashboard.processPluginConfigurationUpdateResult();
            });
        });
    });

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
