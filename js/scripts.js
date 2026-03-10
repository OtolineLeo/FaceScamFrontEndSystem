const video = document.getElementById('myVideo');
const butao = document.getElementById('myBtn');

myBtn.addEventListener("click", () => {

    if(!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia){
        alert('Seu navegador não suporta este recurso');
        return;
    }

    navigator.mediaDevices.getUserMedia({video: true}).
    then(function (stream) {
        video.srcObject = stream;
        video.play();
    })
    .catch(function(err){
        console.log("Erro: " + err);
    });
})