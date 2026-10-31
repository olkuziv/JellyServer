using System.Net;

namespace JellyServer.Plugins;

/// <summary>
/// The page jellyfin-web loads for a plugin (<c>/web/ConfigurationPage?name=…</c>). It is the same for every plugin: the script fetches the
/// plugin's settings description from <c>/Plugins/{id}/Configuration</c> and builds the form from it, so no plugin ships HTML or script.
/// </summary>
internal static class PluginSettingsPage
{
    public static string Render(string pluginName, Guid pluginId) =>
        Template.Replace("__TITLE__", WebUtility.HtmlEncode(pluginName)).Replace("__ID__", pluginId.ToString("N"));

    private const string Template = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="utf-8">
            <title>__TITLE__</title>
        </head>
        <body>
        <div id="pluginSettingsPage" data-role="page" class="page type-interior pluginConfigurationPage" data-title="__TITLE__" data-require="emby-input,emby-button,emby-select,emby-checkbox">
            <div data-role="content">
                <div class="content-primary">
                    <form id="pluginSettingsForm" style="max-width: 50em;">
                        <div id="pluginSettingsHost"></div>
                        <div style="margin-top: 1.5em;">
                            <button is="emby-button" type="submit" class="raised button-submit block"><span>Save</span></button>
                        </div>
                    </form>
                </div>
            </div>
            <script type="text/javascript">
                (function () {
                    var pluginId = '__ID__';
                    var page = document.getElementById('pluginSettingsPage');
                    var host = page.querySelector('#pluginSettingsHost');
                    var form = page.querySelector('#pluginSettingsForm');
                    var MASK = '********';

                    function esc(s) {
                        return String(s == null ? '' : s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
                    }

                    function configUrl() { return ApiClient.getUrl('Plugins/' + pluginId + '/Configuration'); }

                    function hint(f) {
                        var parts = [];
                        if (f.Description) parts.push(esc(f.Description));
                        if (f.Type !== 'Secret' && f.Type !== 'Toggle' && f.Default !== '') parts.push('Default: ' + esc(f.Default));
                        if (f.Type === 'Toggle' && f.Default === 'true') parts.push('Default: on');
                        if (f.RestartRequired) parts.push('Applies after a restart.');
                        return parts.length ? '<div class="fieldDescription">' + parts.join(' ') + '</div>' : '';
                    }

                    function fieldHtml(f) {
                        var key = 'data-key="' + esc(f.Key) + '"';
                        if (f.Type === 'Toggle') {
                            return '<div class="checkboxContainer checkboxContainer-withDescription"><label class="emby-checkbox-label">' +
                                '<input is="emby-checkbox" type="checkbox" ' + key + (f.Value === 'true' ? ' checked' : '') + ' />' +
                                '<span>' + esc(f.Label) + '</span></label>' + hint(f).replace('fieldDescription', 'fieldDescription checkboxFieldDescription') + '</div>';
                        }
                        if (f.Type === 'Select') {
                            var options = (f.Options || []).map(function (o) {
                                return '<option value="' + esc(o) + '"' + (o.toLowerCase() === String(f.Value).toLowerCase() ? ' selected' : '') + '>' + esc(o) + '</option>';
                            }).join('');
                            return '<div class="selectContainer"><select is="emby-select" label="' + esc(f.Label) + '" ' + key + '>' + options + '</select>' + hint(f) + '</div>';
                        }
                        var type = f.Type === 'Number' ? 'number' : (f.Type === 'Secret' ? 'password' : 'text');
                        var bounds = (f.Min != null ? ' min="' + f.Min + '"' : '') + (f.Max != null ? ' max="' + f.Max + '"' : '') + (f.Type === 'Number' ? ' step="1"' : '');
                        return '<div class="inputContainer"><input is="emby-input" type="' + type + '" label="' + esc(f.Label) + '" ' + key + bounds +
                            ' value="' + esc(f.Value) + '"' + (f.Type === 'Secret' ? ' autocomplete="new-password"' : '') + ' />' + hint(f) + '</div>';
                    }

                    function render(model) {
                        host.innerHTML = model.Sections.map(function (s) {
                            return '<div class="verticalSection"><h2 class="sectionTitle">' + esc(s.Title) + '</h2>' +
                                (s.Description ? '<div class="fieldDescription">' + esc(s.Description) + '</div>' : '') +
                                s.Fields.map(fieldHtml).join('') + '</div>';
                        }).join('');
                    }

                    function collect() {
                        var values = {};
                        host.querySelectorAll('[data-key]').forEach(function (el) {
                            values[el.getAttribute('data-key')] = el.type === 'checkbox' ? (el.checked ? 'true' : 'false') : el.value;
                        });
                        return { Values: values };
                    }

                    function failed(response) {
                        Dashboard.hideLoadingMsg();
                        var show = function (text) { Dashboard.alert({ title: 'Settings not saved', message: text }); };
                        if (response && response.text) response.text().then(function (t) {
                            try { var p = JSON.parse(t); show(p.detail || p.title || t); } catch (e) { show(t); }
                        }, function () { show('The server rejected the settings.'); });
                        else show('The server could not be reached.');
                    }

                    function load() {
                        Dashboard.showLoadingMsg();
                        ApiClient.ajax({ type: 'GET', url: configUrl(), dataType: 'json' }).then(function (model) {
                            render(model);
                            Dashboard.hideLoadingMsg();
                        }, failed);
                    }

                    page.addEventListener('pageshow', load);
                    form.addEventListener('submit', function (e) {
                        e.preventDefault();
                        Dashboard.showLoadingMsg();
                        ApiClient.ajax({ type: 'POST', url: configUrl(), data: JSON.stringify(collect()), contentType: 'application/json' })
                            .then(function () { Dashboard.processPluginConfigurationUpdateResult(); load(); }, failed);
                        return false;
                    });
                })();
            </script>
        </div>
        </body>
        </html>
        """;
}
