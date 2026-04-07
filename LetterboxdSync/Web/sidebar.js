(function() {
    function addLbLink() {
        if (document.getElementById("lb-nav-link")) return;
        var settingsLink = document.querySelector(".btnSettings");
        if (!settingsLink) return;
        var link = document.createElement("a");
        link.id = "lb-nav-link";
        link.setAttribute("is", "emby-linkbutton");
        link.className = "navMenuOption lnkMediaFolder";
        link.href = "#";
        link.innerHTML = '<span class="material-icons navMenuOptionIcon movie_filter" aria-hidden="true"></span><span class="navMenuOptionText">Letterboxd</span>';
        link.addEventListener("click", function(e) {
            e.preventDefault();
            e.stopPropagation();
            window.location.assign("/web/configurationpage?name=letterboxduser");
        });
        settingsLink.parentElement.insertBefore(link, settingsLink);
    }
    setInterval(addLbLink, 2000);
    if (document.readyState === "complete") {
        setTimeout(addLbLink, 500);
    } else {
        window.addEventListener("load", function() { setTimeout(addLbLink, 500); });
    }
})();
