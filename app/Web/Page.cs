static class Page
{
    public const string Html = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>Guestbook - Aspire on Locally</title>
          <style>
            body { font-family: system-ui, sans-serif; max-width: 60rem; margin: 2rem auto; padding: 0 1rem; color: #1f2328; }
            form { display: flex; gap: .5rem; flex-wrap: wrap; margin-bottom: 1.5rem; }
            input, button { font: inherit; padding: .4rem .6rem; }
            input[name=message] { flex: 1; min-width: 12rem; }
            table { border-collapse: collapse; width: 100%; }
            th, td { text-align: left; padding: .4rem .6rem; border-bottom: 1px solid #d0d7de; vertical-align: top; }
            .Pending { color: #9a6700; } .Approved { color: #1a7f37; } .Rejected { color: #cf222e; }
            small { color: #59636e; }
          </style>
        </head>
        <body>
          <h1>Guestbook</h1>
          <p>Sign the guestbook and watch the moderator approve your entry. Mention <code>spam</code> to have it rejected.</p>
          <form id="sign">
            <input name="name" placeholder="Name" maxlength="50" required>
            <input name="message" placeholder="Message" maxlength="500" required>
            <button>Sign</button>
          </form>
          <table>
            <thead><tr><th>Name</th><th>Message</th><th>Signed</th><th>State</th></tr></thead>
            <tbody id="entries"><tr><td colspan="4"><small>Loading...</small></td></tr></tbody>
          </table>
          <p><small id="cached"></small></p>
          <script>
            const body = document.getElementById("entries");
            const cached = document.getElementById("cached");
            function row(...cells) {
              const tr = document.createElement("tr");
              for (const value of cells) {
                const td = document.createElement("td");
                td.textContent = value;
                tr.appendChild(td);
              }
              return tr;
            }
            async function refresh() {
              try {
                const response = await (await fetch("/api/entries")).json();
                body.replaceChildren(...response.entries.map(e => {
                  const tr = row(e.name, e.message, new Date(e.createdAt).toLocaleTimeString(), e.state);
                  tr.children[3].className = e.state;
                  return tr;
                }));
                if (response.entries.length === 0) body.innerHTML = '<tr><td colspan="4"><small>No entries yet.</small></td></tr>';
                cached.textContent = `List built by the api at ${new Date(response.cachedAt).toLocaleTimeString()}, and served from the Redis output cache since.`;
              } catch (e) {
                body.innerHTML = '<tr><td colspan="4"><small>Couldn\'t load the guestbook.</small></td></tr>';
              }
            }
            document.getElementById("sign").addEventListener("submit", async event => {
              event.preventDefault();
              const form = event.target;
              const response = await fetch("/api/entries", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(Object.fromEntries(new FormData(form))),
              });
              if (!response.ok) { alert(await response.text()); return; }
              form.reset();
              refresh();
            });
            refresh();
            setInterval(refresh, 2000);
          </script>
        </body>
        </html>
        """;
}
