$(document).ready(function () {
    if (isMobile) {
        $("#sidebarToggleTop").click();
    }
    experty.prepareToCopyImage();
});

var experty = {
    setDamaged: function (data) {
        $("[name=Damageds]").prepend(data);
        const dropdownId = $("[name=Damageds]").attr("id");
        let $dropdown = $("#" + dropdownId);
        const dropdownHtml = $dropdown.html();
        let newHtml = "<select";
        $dropdown.each(function () {
            $.each(this.attributes, function (i, a) {
                newHtml += ` ${a.name}="${a.value}"`;
            })
        });
        newHtml = `${newHtml}>${dropdownHtml}</select>`;
        const selecteds = $dropdown.val();
        const $container = $dropdown.parent().parent();
        $dropdown.parent().remove();
        $container.html(newHtml);
        $dropdown = $("#" + dropdownId);
        $dropdown.val(selecteds);
        let instance = Rystem.instances.find((e) => e.id == dropdownId);
        instance.show();
    },
    redirectAfterUpload: function (a, id, c) {
        document.location = `${document.location.origin}/Accident/Edit/${id}`;
    },
    prepareToCopyImage: function () {
        $(".copyable").click(function (e) {
            console.log($(this).attr("src"));
            navigator.clipboard.write([
                new ClipboardItem({
                    'image/png': $(this).attr("src")
                })
            ]);
            alert("image copied!");
        });
    }
}