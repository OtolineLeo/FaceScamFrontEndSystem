const video = document.getElementById('myVideo');
const butao = document.getElementById('myBtn');

myBtn.addEventListener("click", () => {

    // mediaDevices é uma api que permite acessar camera, mic e tela
    // getUserMedia é a operação que pede acesso à camera ou mix

    if(!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia){
        alert('Seu navegador não suporta este recurso');
        return;
    }

    // getUserMedia aqui é uma operação que vai terminar no futuro

    navigator.mediaDevices.getUserMedia({video: true, audio: true}).
    then(function (stream) {
        video.srcObject = stream;
        video.play();
    })
    .catch(function(err){
        console.log("Erro: " + err);
    });
})