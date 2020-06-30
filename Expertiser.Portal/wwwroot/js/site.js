$(document).ready(function () {
    if (isMobile) {
        $("#sidebarToggleTop").click();
    }
});

var experty = {
    setDamaged: function (data) {
        $("[name=Damageds]").append(data);
    }
}