const PluginId = 'c7a3e1b9-5d42-4f8a-9c06-2b7d8e4f1a35';
let jellyfinUsers = [];

async function loadUsers() {
    const response = await ApiClient.getUsers();
    jellyfinUsers = response;
}

function createAccountEntry(account, container) {
    const template = document.getElementById('accountTemplate');
    const clone = template.content.cloneNode(true);
    const entry = clone.querySelector('.account-entry');

    const userSelect = entry.querySelector('.jellyfinUser');
    jellyfinUsers.forEach(u => {
        const opt = document.createElement('option');
        opt.value = u.Id.replace(/-/g, '');
        opt.textContent = u.Name;
        userSelect.appendChild(opt);
    });

    if (account) {
        userSelect.value = account.UserJellyfinId || '';
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

    container.appendChild(entry);
}

function collectAccounts() {
    const entries = document.querySelectorAll('.account-entry');
    const accounts = [];

    entries.forEach(entry => {
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

async function loadConfig() {
    await loadUsers();

    const config = await ApiClient.getPluginConfiguration(PluginId);
    const container = document.getElementById('accountsList');
    container.innerHTML = '';

    if (config.Accounts && config.Accounts.length > 0) {
        config.Accounts.forEach(account => createAccountEntry(account, container));
    }
}

async function saveConfig() {
    const config = await ApiClient.getPluginConfiguration(PluginId);
    config.Accounts = collectAccounts();

    await ApiClient.updatePluginConfiguration(PluginId, config);
    Dashboard.processPluginConfigurationUpdateResult();
}

document.getElementById('addAccountBtn').addEventListener('click', function () {
    createAccountEntry(null, document.getElementById('accountsList'));
});

document.getElementById('saveBtn').addEventListener('click', saveConfig);

loadConfig();
