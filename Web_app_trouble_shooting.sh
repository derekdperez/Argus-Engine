echo "sudo docker compose -f deployment/docker-compose.yml ps command-center"
sudo docker compose -f deployment/docker-compose.yml ps command-center

echo "sudo docker compose -f deployment/docker-compose.yml logs --tail=150 -f command-center"
sudo docker compose -f deployment/docker-compose.yml logs --tail=150 -f command-center

echo "sudo docker compose -f deployment/docker-compose.yml restart command-center"
docker compose -f deployment/docker-compose.yml restart command-center

echo "sudo docker compose -f deployment/docker-compose.yml logs -f --tail=80 command-center"
sudo docker compose -f deployment/docker-compose.yml logs -f --tail=80 command-center

