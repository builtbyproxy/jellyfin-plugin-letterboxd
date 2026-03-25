const PluginId = 'c7a3e1b9-5d42-4f8a-9c06-2b7d8e4f1a35';
let jellyfinUsers = [];
let initialized = false;

function getUserOptions() {
    return jellyfinUsers.map(u =>
        '<option value="' + u.Id.replace(/-/g, '') + '">' + u.Name + '</option>'
    ).join('');
}

function accountHtml() {
    return '<div class="account-entry verticalSection" style="border: 1px solid #333; padding: 1em; margin-bottom: 1em; border-radius: 4px;">'
        + '<div style="display: flex; justify-content: space-between; align-items: center;">'
        + '<h4 style="margin: 0;">Account</h4>'
        + '<button type="button" class="raised button-cancel removeAccountBtn" style="font-size: 0.8em;">Remove</button>'
        + '</div>'
        + '<div class="inputContainer" style="margin-top: 0.5em;">'
        + '<label class="inputLabel">Jellyfin User</label>'
        + '<select class="jellyfinUser">' + getUserOptions() + '</select>'
        + '</div>'
        + '<div class="inputContainer">'
        + '<label class="inputLabel">Letterboxd Username</label>'
        + '<input type="text" class="lbUsername" />'
        + '</div>'
        + '<div class="inputContainer">'
        + '<label class="inputLabel">Letterboxd Password</label>'
        + '<input type="password" class="lbPassword" />'
        + '</div>'
        + '<div class="inputContainer">'
        + '<label class="inputLabel">Raw Cookies (optional, for Cloudflare bypass)</label>'
        + '<input type="text" class="rawCookies" placeholder="Paste cookie header from browser if login gets 403" />'
        + '</div>'
        + '<div class="checkboxContainer" style="margin-top: 0.5em;">'
        + '<label><input type="checkbox" class="accountEnabled" /> Enabled</label>'
        + '</div>'
        + '<div class="checkboxContainer">'
        + '<label><input type="checkbox" class="syncFavorites" /> Sync favorites as liked</label>'
        + '</div>'
        + '<div class="checkboxContainer">'
        + '<label><input type="checkbox" class="enableDateFilter" /> Only sync recently played</label>'
        + '</div>'
        + '<div class="inputContainer dateFilterDaysContainer" style="display: none;">'
        + '<label class="inputLabel">Days to look back</label>'
        + '<input type="number" class="dateFilterDays" min="1" max="365" value="7" />'
        + '</div>'
        + '</div>';
}

function addAccountEntry(account) {
    var container = document.getElementById('accountsList');
    container.insertAdjacentHTML('beforeend', accountHtml());
    var entry = container.lastElementChild;

    if (account) {
        entry.querySelector('.jellyfinUser').value = account.UserJellyfinId || '';
        entry.querySelector('.lbUsername').value = account.LetterboxdUsername || '';
        entry.querySelector('.lbPassword').value = account.LetterboxdPassword || '';
        entry.querySelector('.rawCookies').value = account.RawCookies || '';
        entry.querySelector('.accountEnabled').checked = account.Enabled !== false;
        entry.querySelector('.syncFavorites').checked = account.SyncFavorites === true;
        entry.querySelector('.enableDateFilter').checked = account.EnableDateFilter === true;
        entry.querySelector('.dateFilterDays').value = account.DateFilterDays || 7;
        if (account.EnableDateFilter) {
            entry.querySelector('.dateFilterDaysContainer').style.display = '';
        }
    } else {
        entry.querySelector('.accountEnabled').checked = true;
    }

    entry.querySelector('.enableDateFilter').addEventListener('change', function () {
        entry.querySelector('.dateFilterDaysContainer').style.display = this.checked ? '' : 'none';
    });

    entry.querySelector('.removeAccountBtn').addEventListener('click', function () {
        entry.remove();
    });
}

function collectAccounts() {
    var entries = document.querySelectorAll('.account-entry');
    var accounts = [];
    entries.forEach(function (entry) {
        accounts.push({
            UserJellyfinId: entry.querySelector('.jellyfinUser').value,
            LetterboxdUsername: entry.querySelector('.lbUsername').value,
            LetterboxdPassword: entry.querySelector('.lbPassword').value,
            RawCookies: entry.querySelector('.rawCookies').value || null,
            Enabled: entry.querySelector('.accountEnabled').checked,
            SyncFavorites: entry.querySelector('.syncFavorites').checked,
            EnableDateFilter: entry.querySelector('.enableDateFilter').checked,
            DateFilterDays: parseInt(entry.querySelector('.dateFilterDays').value, 10) || 7
        });
    });
    return accounts;
}

function init() {
    if (initialized) return;
    initialized = true;

    console.log('[LetterboxdSync] Config page initializing...');

    ApiClient.getUsers().then(function (users) {
        jellyfinUsers = users;
        console.log('[LetterboxdSync] Loaded ' + users.length + ' users');

        ApiClient.getPluginConfiguration(PluginId).then(function (config) {
            var container = document.getElementById('accountsList');
            container.innerHTML = '';

            if (config.Accounts && config.Accounts.length > 0) {
                config.Accounts.forEach(function (account) {
                    addAccountEntry(account);
                });
            }
            console.log('[LetterboxdSync] Loaded ' + (config.Accounts ? config.Accounts.length : 0) + ' accounts');
        });
    });

    document.getElementById('addAccountBtn').addEventListener('click', function () {
        console.log('[LetterboxdSync] Add account clicked');
        addAccountEntry(null);
    });

    document.getElementById('saveBtn').addEventListener('click', function () {
        ApiClient.getPluginConfiguration(PluginId).then(function (config) {
            config.Accounts = collectAccounts();
            ApiClient.updatePluginConfiguration(PluginId, config).then(function () {
                Dashboard.processPluginConfigurationUpdateResult();
            });
        });
    });
}

init();
